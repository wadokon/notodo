// NotoDo - Notionと双方向同期するデスクトップTODOウィジェット (Windows版)
// PCのウィジェットとNotionデータベースを双方向同期する。
// 設定: exeと同じフォルダの notion-config.json に Token / DatabaseId を記述。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NotoDo
{
    static class Program
    {
        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mtx = new Mutex(true, "Global\\NotoDoMutex", out created))
            {
                if (!created) return;
                // Notion APIはTLS1.2必須
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log(e.ExceptionObject.ToString());
                };
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log(e.Exception.ToString());
                };
                try { Application.Run(new TrayContext()); }
                catch (Exception ex) { Log(ex.ToString()); }
            }
        }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDir, "notodo-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + "\r\n");
            }
            catch { }
        }
    }

    class TodoItem
    {
        public string NotionId;  // 未同期の項目は null
        public string Text;
        public bool Done;
        public string DoneAt;    // 完了日 "yyyy-MM-dd"（未完了は null）
        public bool Hidden;      // Notionの「デスクトップ非表示」チェック
    }

    class TodoStore
    {
        public List<TodoItem> Items = new List<TodoItem>();
        readonly string path;
        public event Action Changed;

        public TodoStore()
        {
            path = Path.Combine(Program.AppDir, "todos.json");
            Load();
        }

        public void Fire()
        {
            var h = Changed;
            if (h != null) h();
        }

        public int RemainCount()
        {
            return Items.Count(x => !x.Done && !x.Hidden);
        }

        // 表示対象: 未完了すべて + 今日完了した項目（昨日以前の完了分は翌日から非表示、データは残る）
        // 追加が新しいものを上に（Itemsは作成日時の昇順なのでReverseで降順にする）、
        // 完了済みは一番下にまとめる（OrderByは安定ソートなのでグループ内の並びは維持される）
        public List<TodoItem> VisibleItems()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            return Items.Where(x => !x.Hidden)
                .Where(x => !x.Done || x.DoneAt == null ||
                    string.Compare(x.DoneAt, today, StringComparison.Ordinal) >= 0)
                .Reverse().OrderBy(x => x.Done ? 1 : 0).ToList();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                var ser = new JavaScriptSerializer();
                var arr = ser.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as object[];
                if (arr == null) return;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("Text")) continue;
                    var item = new TodoItem();
                    item.Text = (string)d["Text"];
                    item.Done = d.ContainsKey("Done") && d["Done"] is bool && (bool)d["Done"];
                    // 旧フォーマットにはIdが無い
                    if (d.ContainsKey("Id") && d["Id"] is string) item.NotionId = (string)d["Id"];
                    if (d.ContainsKey("DoneAt") && d["DoneAt"] is string) item.DoneAt = (string)d["DoneAt"];
                    if (d.ContainsKey("Hidden") && d["Hidden"] is bool) item.Hidden = (bool)d["Hidden"];
                    Items.Add(item);
                }
            }
            catch (Exception ex) { Program.Log("todos.json load: " + ex.Message); }
        }

        public void Save()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var list = new List<Dictionary<string, object>>();
                foreach (var it in Items)
                {
                    var d = new Dictionary<string, object>();
                    d["Id"] = it.NotionId;
                    d["Text"] = it.Text;
                    d["Done"] = it.Done;
                    d["DoneAt"] = it.DoneAt;
                    d["Hidden"] = it.Hidden;
                    list.Add(d);
                }
                File.WriteAllText(path, ser.Serialize(list), new UTF8Encoding(false));
            }
            catch (Exception ex) { Program.Log("todos.json save: " + ex.Message); }
        }
    }

    enum SyncStatus { NoConfig, Ok, Syncing, Error }

    class SyncEngine : IDisposable
    {
        class PushOp
        {
            public TodoItem Item;
            public string Id;
            public string Text;
            public bool Done;
            public string DoneAt;
            public bool Hidden;
        }

        readonly TodoStore store;
        readonly Control ui;                 // UIスレッドへのマーシャリング用
        readonly Thread worker;
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly object qLock = new object();
        readonly Dictionary<TodoItem, PushOp> ops = new Dictionary<TodoItem, PushOp>();
        readonly List<string> archives = new List<string>();
        volatile bool stop;
        bool forcePoll = true;
        DateTime lastPoll = DateTime.MinValue;
        DateTime errorUntil = DateTime.MinValue;

        string token = "";
        string databaseId = "";
        DateTime cfgTime = DateTime.MinValue;
        string titleProp, doneProp, dateProp, hideProp;

        public SyncStatus Status = SyncStatus.NoConfig;
        public string StatusDetail = "";
        public DateTime LastOkTime = DateTime.MinValue;
        public event Action StatusChanged;
        public string DatabaseId { get { return databaseId; } }

        const int PollIntervalSec = 60;

        public SyncEngine(TodoStore store, Control ui)
        {
            this.store = store;
            this.ui = ui;
            worker = new Thread(Loop);
            worker.IsBackground = true;
        }

        public void Start() { worker.Start(); }

        public void NotifyChanged(TodoItem item)
        {
            lock (qLock)
            {
                var op = new PushOp();
                op.Item = item;
                op.Id = item.NotionId;
                op.Text = item.Text;
                op.Done = item.Done;
                op.DoneAt = item.DoneAt;
                op.Hidden = item.Hidden;
                ops[item] = op;
            }
            signal.Set();
        }

        public void NotifyDeleted(TodoItem item)
        {
            lock (qLock)
            {
                ops.Remove(item);
                if (item.NotionId != null) archives.Add(item.NotionId);
            }
            signal.Set();
        }

        public void RequestSync()
        {
            forcePoll = true;
            errorUntil = DateTime.MinValue;
            signal.Set();
        }

        void SetStatus(SyncStatus st, string detail)
        {
            Status = st;
            StatusDetail = detail;
            if (st == SyncStatus.Ok) LastOkTime = DateTime.Now;
            var h = StatusChanged;
            if (h != null) OnUi(delegate { if (h != null) h(); });
        }

        void OnUi(Action a)
        {
            try
            {
                if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a);
            }
            catch { }
        }

        void Loop()
        {
            while (!stop)
            {
                signal.WaitOne(2000);
                if (stop) break;
                try
                {
                    LoadConfig();
                    if (token == "" || databaseId == "")
                    {
                        SetStatus(SyncStatus.NoConfig, "notion-config.json の Token が未設定です");
                        continue;
                    }
                    if (DateTime.Now < errorUntil) continue;
                    if (titleProp == null) LoadSchema();

                    bool pushed = ProcessQueue();
                    if (pushed) forcePoll = true;

                    bool hasPending;
                    lock (qLock) hasPending = ops.Count > 0 || archives.Count > 0;

                    if (!hasPending && (forcePoll || (DateTime.Now - lastPoll).TotalSeconds >= PollIntervalSec))
                    {
                        forcePoll = false;
                        var remote = QueryAll();
                        lastPoll = DateTime.Now;
                        OnUi(delegate { ApplyRemote(remote); });
                        SetStatus(SyncStatus.Ok, "");
                    }
                }
                catch (Exception ex)
                {
                    errorUntil = DateTime.Now.AddSeconds(30);
                    titleProp = null; // スキーマ変更が原因の可能性もあるので次回再取得
                    SetStatus(SyncStatus.Error, ex.Message);
                }
            }
        }

        bool ProcessQueue()
        {
            List<PushOp> batch;
            List<string> arch;
            lock (qLock)
            {
                batch = ops.Values.ToList();
                ops.Clear();
                arch = archives.ToList();
                archives.Clear();
            }
            if (batch.Count == 0 && arch.Count == 0) return false;
            SetStatus(SyncStatus.Syncing, "");

            foreach (var id in arch)
            {
                try { ArchivePage(id); }
                catch
                {
                    lock (qLock) archives.Add(id);
                    throw;
                }
            }
            foreach (var op in batch)
            {
                try
                {
                    if (op.Id == null)
                    {
                        string newId = CreatePage(op);
                        var item = op.Item;
                        OnUi(delegate { item.NotionId = newId; store.Save(); });
                    }
                    else
                    {
                        UpdatePage(op);
                    }
                }
                catch
                {
                    // 失敗した操作は残す（その間に新しい操作が積まれていたらそちらを優先）
                    lock (qLock) { if (!ops.ContainsKey(op.Item)) ops[op.Item] = op; }
                    throw;
                }
            }
            return true;
        }

        class RemoteItem { public string Id; public string Text; public bool Done; public string DoneAt; public string LastEdited; public bool Hidden; }

        void ApplyRemote(List<RemoteItem> remote)
        {
            lock (qLock)
            {
                // ローカル変更が滞留している間はリモート反映を見送る（次回に持ち越し）
                if (ops.Count > 0 || archives.Count > 0) return;
            }
            var newList = new List<TodoItem>();
            var rest = store.Items.ToList();
            foreach (var r in remote)
            {
                TodoItem local = rest.FirstOrDefault(x => x.NotionId == r.Id);
                if (local == null) local = rest.FirstOrDefault(x => x.NotionId == null && x.Text == r.Text);
                if (local != null)
                {
                    rest.Remove(local);
                    local.NotionId = r.Id;
                    local.Text = r.Text;
                    local.Done = r.Done;
                    local.DoneAt = r.DoneAt;
                    local.Hidden = r.Hidden;
                    FixDoneAt(local, r);
                    newList.Add(local);
                }
                else
                {
                    var it = new TodoItem();
                    it.NotionId = r.Id;
                    it.Text = r.Text;
                    it.Done = r.Done;
                    it.DoneAt = r.DoneAt;
                    it.Hidden = r.Hidden;
                    FixDoneAt(it, r);
                    newList.Add(it);
                }
            }
            foreach (var l in rest)
            {
                if (l.NotionId == null)
                {
                    // ローカルにしか無い項目 → Notionへアップロード
                    newList.Add(l);
                    NotifyChanged(l);
                }
                // NotionId有りでリモートに無い → リモート側で削除されたので破棄
            }
            store.Items = newList;
            store.Save();
            store.Fire();
        }

        // 「完了⇔完了日」の整合を取る。スマホのNotionアプリはチェックだけ変えて完了日を
        // 触らないので、こちらで補完（完了日はページ最終編集時刻から推定）・クリアして書き戻す。
        void FixDoneAt(TodoItem item, RemoteItem r)
        {
            if (item.Done && item.DoneAt == null)
            {
                item.DoneAt = LastEditedDate(r);
                NotifyChanged(item);
            }
            else if (!item.Done && item.DoneAt != null)
            {
                item.DoneAt = null;
                NotifyChanged(item);
            }
        }

        static string LastEditedDate(RemoteItem r)
        {
            try
            {
                if (r.LastEdited != null)
                    return DateTime.Parse(r.LastEdited, null,
                        System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("yyyy-MM-dd");
            }
            catch { }
            return DateTime.Today.ToString("yyyy-MM-dd");
        }

        // ---- Notion API ----

        void LoadConfig()
        {
            var p = Path.Combine(Program.AppDir, "notion-config.json");
            if (!File.Exists(p)) { token = ""; return; }
            var t = File.GetLastWriteTime(p);
            if (t == cfgTime) return;
            cfgTime = t;
            titleProp = null;
            var ser = new JavaScriptSerializer();
            var d = ser.DeserializeObject(File.ReadAllText(p, Encoding.UTF8)) as Dictionary<string, object>;
            token = "";
            databaseId = "";
            if (d != null)
            {
                if (d.ContainsKey("Token") && d["Token"] is string) token = ((string)d["Token"]).Trim();
                if (d.ContainsKey("DatabaseId") && d["DatabaseId"] is string) databaseId = ((string)d["DatabaseId"]).Trim().Replace("-", "");
            }
        }

        void LoadSchema()
        {
            var ser = new JavaScriptSerializer();
            var resp = ser.DeserializeObject(Request("GET", "https://api.notion.com/v1/databases/" + databaseId, null)) as Dictionary<string, object>;
            titleProp = "Name";
            doneProp = null;
            dateProp = null;
            hideProp = null;
            var props = resp.ContainsKey("properties") ? resp["properties"] as Dictionary<string, object> : null;
            if (props != null)
            {
                var checkboxes = new List<string>();
                foreach (var kv in props)
                {
                    var pd = kv.Value as Dictionary<string, object>;
                    if (pd == null || !pd.ContainsKey("type")) continue;
                    var ty = (string)pd["type"];
                    if (ty == "title") titleProp = kv.Key;
                    else if (ty == "checkbox") checkboxes.Add(kv.Key);
                    else if (ty == "date" && dateProp == null) dateProp = kv.Key;
                }
                // チェックボックスが複数あるため名前で役割を割り当てる
                doneProp = checkboxes.Contains("Done") ? "Done"
                    : (checkboxes.Count > 0 ? checkboxes[0] : null);
                hideProp = checkboxes.Contains("デスクトップ非表示") ? "デスクトップ非表示"
                    : checkboxes.FirstOrDefault(c => c != doneProp);
            }
        }

        List<RemoteItem> QueryAll()
        {
            var ser = new JavaScriptSerializer();
            var result = new List<RemoteItem>();
            string cursor = null;
            do
            {
                var body = new Dictionary<string, object>();
                body["page_size"] = 100;
                body["sorts"] = new object[] {
                    new Dictionary<string, object> { { "timestamp", "created_time" }, { "direction", "ascending" } }
                };
                if (cursor != null) body["start_cursor"] = cursor;
                var resp = ser.DeserializeObject(Request("POST",
                    "https://api.notion.com/v1/databases/" + databaseId + "/query",
                    ser.Serialize(body))) as Dictionary<string, object>;
                var results = resp["results"] as object[];
                foreach (var o in results)
                {
                    var page = o as Dictionary<string, object>;
                    var props = page["properties"] as Dictionary<string, object>;
                    var item = new RemoteItem();
                    item.Id = (string)page["id"];
                    item.Text = "";
                    if (props.ContainsKey(titleProp))
                    {
                        var tp = props[titleProp] as Dictionary<string, object>;
                        var arr = tp != null && tp.ContainsKey("title") ? tp["title"] as object[] : null;
                        if (arr != null)
                        {
                            var sb = new StringBuilder();
                            foreach (var seg in arr)
                            {
                                var sd = seg as Dictionary<string, object>;
                                if (sd != null && sd.ContainsKey("plain_text")) sb.Append((string)sd["plain_text"]);
                            }
                            item.Text = sb.ToString();
                        }
                    }
                    if (doneProp != null && props.ContainsKey(doneProp))
                    {
                        var dp = props[doneProp] as Dictionary<string, object>;
                        if (dp != null && dp.ContainsKey("checkbox") && dp["checkbox"] is bool) item.Done = (bool)dp["checkbox"];
                    }
                    if (hideProp != null && props.ContainsKey(hideProp))
                    {
                        var hp = props[hideProp] as Dictionary<string, object>;
                        if (hp != null && hp.ContainsKey("checkbox") && hp["checkbox"] is bool) item.Hidden = (bool)hp["checkbox"];
                    }
                    if (dateProp != null && props.ContainsKey(dateProp))
                    {
                        var pp = props[dateProp] as Dictionary<string, object>;
                        var dv = pp != null && pp.ContainsKey("date") ? pp["date"] as Dictionary<string, object> : null;
                        if (dv != null && dv.ContainsKey("start") && dv["start"] is string)
                        {
                            var s = (string)dv["start"];
                            item.DoneAt = s.Length >= 10 ? s.Substring(0, 10) : s;
                        }
                    }
                    if (page.ContainsKey("last_edited_time") && page["last_edited_time"] is string)
                        item.LastEdited = (string)page["last_edited_time"];
                    if (item.Text.Length > 0) result.Add(item);
                }
                cursor = resp.ContainsKey("has_more") && (bool)resp["has_more"] ? (string)resp["next_cursor"] : null;
            } while (cursor != null);
            return result;
        }

        Dictionary<string, object> BuildProps(PushOp op)
        {
            var props = new Dictionary<string, object>();
            props[titleProp] = new Dictionary<string, object> {
                { "title", new object[] { new Dictionary<string, object> {
                    { "text", new Dictionary<string, object> { { "content", op.Text } } } } } }
            };
            if (doneProp != null)
                props[doneProp] = new Dictionary<string, object> { { "checkbox", op.Done } };
            if (dateProp != null)
                props[dateProp] = new Dictionary<string, object> {
                    { "date", op.DoneAt == null ? null : (object)new Dictionary<string, object> { { "start", op.DoneAt } } }
                };
            if (hideProp != null)
                props[hideProp] = new Dictionary<string, object> { { "checkbox", op.Hidden } };
            return props;
        }

        string CreatePage(PushOp op)
        {
            var ser = new JavaScriptSerializer();
            var body = new Dictionary<string, object>();
            body["parent"] = new Dictionary<string, object> { { "database_id", databaseId } };
            body["properties"] = BuildProps(op);
            var resp = ser.DeserializeObject(Request("POST", "https://api.notion.com/v1/pages", ser.Serialize(body))) as Dictionary<string, object>;
            return (string)resp["id"];
        }

        void UpdatePage(PushOp op)
        {
            var ser = new JavaScriptSerializer();
            var body = new Dictionary<string, object>();
            body["properties"] = BuildProps(op);
            Request("PATCH", "https://api.notion.com/v1/pages/" + op.Id, ser.Serialize(body));
        }

        void ArchivePage(string id)
        {
            Request("PATCH", "https://api.notion.com/v1/pages/" + id, "{\"archived\":true}");
        }

        string Request(string method, string url, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["Notion-Version"] = "2022-06-28";
            req.Timeout = 15000;
            req.UserAgent = "NotoDo/1.0";
            if (body != null)
            {
                req.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                string detail = ex.Message;
                try
                {
                    var hr = ex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        using (var r = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                        {
                            var ser = new JavaScriptSerializer();
                            var d = ser.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                            if (d != null && d.ContainsKey("message")) detail = (int)hr.StatusCode + ": " + (string)d["message"];
                        }
                    }
                }
                catch { }
                throw new ApplicationException(detail);
            }
        }

        public void Dispose()
        {
            stop = true;
            signal.Set();
        }
    }

    class EditDialog : Form
    {
        readonly TextBox box;
        public string Value { get { return box.Text.Trim(); } }

        public EditDialog(string title, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(340, 76);
            Font = new Font("Segoe UI", 9f);

            box = new TextBox();
            box.Location = new Point(10, 10);
            box.Width = 320;
            box.Text = initial;
            Controls.Add(box);

            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(174, 42);
            ok.Size = new Size(75, 26);
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(255, 42);
            cancel.Size = new Size(75, 26);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        const int HeaderH = 30;
        const int RowH = 26;
        const int AddRowH = 26;
        const int PadBottom = 6;
        const int ResizeEdge = 6;

        readonly TodoStore store;
        public SyncEngine Engine;
        public ContextMenuStrip MainMenu;
        public bool AlwaysOnTop;
        readonly string posPath;
        readonly ToolTip tip = new ToolTip();

        readonly Font fontMain = new Font("Segoe UI", 9f);
        readonly Font fontDone = new Font("Segoe UI", 9f, FontStyle.Strikeout);
        readonly Font fontHeader = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        readonly Font fontSmall = new Font("Segoe UI", 8f);

        int hoverRow = -1;
        bool hoverAdd;
        string tipText = "";
        List<TodoItem> view = new List<TodoItem>();  // 表示中の項目（昨日以前の完了分を除く）

        public WidgetForm(TodoStore store)
        {
            this.store = store;
            posPath = Path.Combine(Program.AppDir, "todo-position.json");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 34, 40);
            DoubleBuffered = true;
            Width = 240;
            MinimumSize = new Size(180, 100);
            MaximumSize = new Size(520, 2000);

            LoadPosition();
            TopMost = AlwaysOnTop;
            RefreshView();

            MouseDown += OnWidgetMouseDown;
            MouseMove += OnWidgetMouseMove;
            MouseLeave += delegate { hoverRow = -1; hoverAdd = false; Invalidate(); };
            MouseDoubleClick += OnWidgetDoubleClick;
            ResizeEnd += delegate { SavePosition(); FitToRows(); Invalidate(); };
            Resize += delegate { UpdateRegion(); Invalidate(); };

            store.Changed += delegate { RefreshView(); };
        }

        void RefreshView()
        {
            view = store.VisibleItems();
            FitToRows();
            Invalidate();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x40000;    // WS_THICKFRAME: 横リサイズ用
                cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW: Alt+Tabに出さない
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // WM_NCCALCSIZE: THICKFRAME由来の枠を消してクライアント領域を全面にする
            if (m.Msg == 0x83 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x84) // WM_NCHITTEST: 右端だけリサイズ可能にする
            {
                int lp = m.LParam.ToInt32();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)(pt.X >= Width - ResizeEdge ? 11 /*HTRIGHT*/ : 1 /*HTCLIENT*/);
                return;
            }
            base.WndProc(ref m);
        }

        void UpdateRegion()
        {
            using (var path = RoundRect(new Rectangle(0, 0, Width, Height), 10))
                Region = new Region(path);
        }

        static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void FitToRows()
        {
            Height = HeaderH + view.Count * RowH + AddRowH + PadBottom;
            UpdateRegion();
        }

        void LoadPosition()
        {
            int x = -99999, y = 0, w = 240;
            AlwaysOnTop = false;
            try
            {
                if (File.Exists(posPath))
                {
                    var ser = new JavaScriptSerializer();
                    var d = ser.DeserializeObject(File.ReadAllText(posPath)) as Dictionary<string, object>;
                    if (d != null)
                    {
                        if (d.ContainsKey("X")) x = Convert.ToInt32(d["X"]);
                        if (d.ContainsKey("Y")) y = Convert.ToInt32(d["Y"]);
                        if (d.ContainsKey("W")) w = Convert.ToInt32(d["W"]);
                        if (d.ContainsKey("TopMost")) AlwaysOnTop = (bool)d["TopMost"];
                    }
                }
            }
            catch { }
            if (w < 180 || w > 520) w = 240;
            Width = w;
            var probe = new Rectangle(x, y, w, 100);
            bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(probe));
            if (x == -99999 || !onScreen)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                x = wa.Right - w - 24;
                y = wa.Top + 80;
            }
            Location = new Point(x, y);
        }

        public void SavePosition()
        {
            try
            {
                File.WriteAllText(posPath, string.Format(
                    "{{\"X\":{0},\"Y\":{1},\"W\":{2},\"TopMost\":{3}}}",
                    Left, Top, Width, AlwaysOnTop ? "true" : "false"), new UTF8Encoding(false));
            }
            catch { }
        }

        int RowAt(Point p)
        {
            if (p.Y < HeaderH) return -1;
            int i = (p.Y - HeaderH) / RowH;
            return i >= 0 && i < view.Count ? i : -1;
        }

        bool InAddRow(Point p)
        {
            int top = HeaderH + view.Count * RowH;
            return p.Y >= top && p.Y < top + AddRowH;
        }

        Rectangle CheckRect(int i)
        {
            return new Rectangle(12, HeaderH + i * RowH + (RowH - 14) / 2, 14, 14);
        }

        Rectangle DeleteRect(int i)
        {
            return new Rectangle(Width - 24, HeaderH + i * RowH + (RowH - 14) / 2, 14, 14);
        }

        void OnWidgetMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int row = RowAt(e.Location);
                if (row >= 0) ShowRowMenu(row);
                else if (MainMenu != null) MainMenu.Show(this, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            int r = RowAt(e.Location);
            if (r >= 0)
            {
                if (CheckRect(r).Contains(e.Location))
                {
                    var item = view[r];
                    item.Done = !item.Done;
                    item.DoneAt = item.Done ? DateTime.Today.ToString("yyyy-MM-dd") : null;
                    store.Save();
                    store.Fire();
                    if (Engine != null) Engine.NotifyChanged(item);
                }
                else if (DeleteRect(r).Contains(e.Location))
                {
                    DeleteRow(r);
                }
                // 行のテキスト部分はダブルクリック編集用に何もしない
                return;
            }
            if (InAddRow(e.Location)) { AddTask(); return; }
            // 余白・ヘッダはドラッグで移動
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
        }

        void OnWidgetMouseMove(object sender, MouseEventArgs e)
        {
            int r = RowAt(e.Location);
            bool a = InAddRow(e.Location);
            if (r != hoverRow || a != hoverAdd)
            {
                hoverRow = r;
                hoverAdd = a;
                Invalidate();
            }
            UpdateTooltip(r);
            Cursor = (e.X >= Width - ResizeEdge) ? Cursors.SizeWE :
                     (r >= 0 || a) ? Cursors.Hand : Cursors.Default;
        }

        void UpdateTooltip(int row)
        {
            string t = "";
            if (row >= 0)
            {
                var item = view[row];
                int avail = Width - 34 - 26;
                if (TextRenderer.MeasureText(item.Text, fontMain).Width > avail) t = item.Text;
            }
            if (t != tipText)
            {
                tipText = t;
                tip.SetToolTip(this, t);
            }
        }

        void OnWidgetDoubleClick(object sender, MouseEventArgs e)
        {
            int r = RowAt(e.Location);
            if (r >= 0 && !CheckRect(r).Contains(e.Location) && !DeleteRect(r).Contains(e.Location))
                EditTask(r);
        }

        void ShowRowMenu(int row)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("編集", null, delegate { EditTask(row); });
            menu.Items.Add("デスクトップで非表示", null, delegate
            {
                var item = view[row];
                item.Hidden = true;
                store.Save();
                store.Fire();
                if (Engine != null) Engine.NotifyChanged(item);
            });
            menu.Items.Add("削除", null, delegate { DeleteRow(row); });
            menu.Show(Cursor.Position);
        }

        void DeleteRow(int row)
        {
            var item = view[row];
            store.Items.Remove(item);
            store.Save();
            store.Fire();
            if (Engine != null) Engine.NotifyDeleted(item);
        }

        public void AddTask()
        {
            using (var dlg = new EditDialog("TODOを追加", ""))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value == "") return;
                var item = new TodoItem();
                item.Text = dlg.Value;
                store.Items.Add(item);
                store.Save();
                store.Fire();
                if (Engine != null) Engine.NotifyChanged(item);
            }
        }

        void EditTask(int row)
        {
            var item = view[row];
            using (var dlg = new EditDialog("TODOを編集", item.Text))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value == "" || dlg.Value == item.Text) return;
                item.Text = dlg.Value;
                store.Save();
                store.Fire();
                if (Engine != null) Engine.NotifyChanged(item);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ヘッダ
            TextRenderer.DrawText(g, "TODO", fontHeader, new Point(12, 7), Color.White);
            string date = DateTime.Now.ToString("M/d (ddd)");
            var dateSize = TextRenderer.MeasureText(date, fontSmall);
            TextRenderer.DrawText(g, date, fontSmall, new Point(Width - dateSize.Width - 24, 9), Color.FromArgb(150, 155, 165));

            // 同期状態ドット
            Color dot = Color.FromArgb(110, 115, 125);
            if (Engine != null)
            {
                switch (Engine.Status)
                {
                    case SyncStatus.Ok: dot = Color.FromArgb(85, 190, 130); break;
                    case SyncStatus.Syncing: dot = Color.FromArgb(230, 180, 70); break;
                    case SyncStatus.Error: dot = Color.FromArgb(225, 95, 95); break;
                }
            }
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, Width - 17, 12, 8, 8);

            using (var line = new Pen(Color.FromArgb(55, 58, 66)))
                g.DrawLine(line, 10, HeaderH - 2, Width - 10, HeaderH - 2);

            // 行
            for (int i = 0; i < view.Count; i++)
            {
                var item = view[i];
                int y = HeaderH + i * RowH;
                if (i == hoverRow)
                    using (var hb = new SolidBrush(Color.FromArgb(48, 51, 60)))
                        g.FillRectangle(hb, 4, y, Width - 8, RowH);

                var cr = CheckRect(i);
                if (item.Done)
                {
                    using (var cb = new SolidBrush(Color.FromArgb(85, 190, 130)))
                        g.FillRectangle(cb, cr);
                    using (var wp = new Pen(Color.White, 1.8f))
                    {
                        g.DrawLine(wp, cr.X + 3, cr.Y + 7, cr.X + 6, cr.Y + 10);
                        g.DrawLine(wp, cr.X + 6, cr.Y + 10, cr.X + 11, cr.Y + 4);
                    }
                }
                else
                {
                    using (var cp = new Pen(Color.FromArgb(120, 125, 135), 1.5f))
                        g.DrawRectangle(cp, cr);
                }

                int textW = Width - 34 - (i == hoverRow ? 26 : 12);
                var rect = new Rectangle(34, y + 4, textW, RowH - 8);
                TextRenderer.DrawText(g, item.Text,
                    item.Done ? fontDone : fontMain, rect,
                    item.Done ? Color.FromArgb(120, 125, 135) : Color.Gainsboro,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                if (i == hoverRow)
                {
                    var dr = DeleteRect(i);
                    using (var xp = new Pen(Color.FromArgb(170, 100, 100), 1.8f))
                    {
                        g.DrawLine(xp, dr.X + 3, dr.Y + 3, dr.Right - 3, dr.Bottom - 3);
                        g.DrawLine(xp, dr.X + 3, dr.Bottom - 3, dr.Right - 3, dr.Y + 3);
                    }
                }
            }

            // 追加行
            int addY = HeaderH + view.Count * RowH;
            TextRenderer.DrawText(g, "＋ 追加", fontMain, new Point(12, addY + 4),
                hoverAdd ? Color.Gainsboro : Color.FromArgb(120, 125, 135));
        }
    }

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        readonly NotifyIcon notify;
        readonly WidgetForm widget;
        readonly TodoStore store;
        readonly SyncEngine engine;
        readonly System.Windows.Forms.Timer midnightTimer = new System.Windows.Forms.Timer();
        IntPtr prevHicon = IntPtr.Zero;

        public TrayContext()
        {
            store = new TodoStore();
            widget = new WidgetForm(store);
            widget.Show();
            var forceHandle = widget.Handle;

            engine = new SyncEngine(store, widget);
            widget.Engine = engine;

            var menu = new ContextMenuStrip();
            var miWidget = new ToolStripMenuItem("ウィジェットを表示");
            miWidget.Checked = true;
            miWidget.Click += delegate
            {
                widget.Visible = !widget.Visible;
                miWidget.Checked = widget.Visible;
            };
            menu.Items.Add(miWidget);

            var miTopMost = new ToolStripMenuItem("最前面に表示");
            miTopMost.Checked = widget.AlwaysOnTop;
            miTopMost.Click += delegate
            {
                widget.AlwaysOnTop = !widget.AlwaysOnTop;
                widget.TopMost = widget.AlwaysOnTop;
                miTopMost.Checked = widget.AlwaysOnTop;
                widget.SavePosition();
            };
            menu.Items.Add(miTopMost);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("今すぐ同期", null, delegate { engine.RequestSync(); });
            menu.Items.Add("Notionで開く", null, delegate
            {
                try
                {
                    string db = engine.DatabaseId;
                    if (db != "") System.Diagnostics.Process.Start("https://www.notion.so/" + db);
                }
                catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, delegate { ExitApp(); });

            widget.MainMenu = menu;

            notify = new NotifyIcon();
            notify.ContextMenuStrip = menu;
            notify.Visible = true;
            notify.DoubleClick += delegate
            {
                widget.Visible = true;
                miWidget.Checked = true;
            };

            store.Changed += UpdateTray;
            engine.StatusChanged += delegate { UpdateTray(); widget.Invalidate(); };
            UpdateTray();

            // 日付が変わったら昨日完了した項目を表示から外す（データはNotion・ローカルとも残す）
            midnightTimer.Tick += delegate
            {
                store.Fire();
                ScheduleMidnight();
            };
            ScheduleMidnight();

            engine.Start();
        }

        void ScheduleMidnight()
        {
            var next = DateTime.Today.AddDays(1);
            var ms = (next - DateTime.Now).TotalMilliseconds + 2000;
            if (ms < 5000) ms = 5000;
            if (ms > int.MaxValue) ms = int.MaxValue;
            midnightTimer.Interval = (int)ms;
            midnightTimer.Start();
        }

        void UpdateTray()
        {
            int n = store.RemainCount();
            SetTrayIcon(n);
            string status;
            switch (engine.Status)
            {
                case SyncStatus.Ok: status = "同期OK " + engine.LastOkTime.ToString("HH:mm"); break;
                case SyncStatus.Syncing: status = "同期中"; break;
                case SyncStatus.Error: status = "同期エラー"; break;
                default: status = "未設定"; break;
            }
            var text = "TODO: " + n + "件 (" + status + ")";
            if (text.Length > 63) text = text.Substring(0, 63);
            notify.Text = text;
        }

        void SetTrayIcon(int count)
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(70, 120, 200)))
                    g.FillEllipse(b, 0, 0, 15, 15);
                string s = count > 9 ? "9+" : count.ToString();
                using (var f = new Font("Segoe UI", count > 9 ? 6f : 8f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(s, f);
                    g.DrawString(s, f, Brushes.White, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f + 0.5f);
                }
                IntPtr h = bmp.GetHicon();
                notify.Icon = Icon.FromHandle(h);
                if (prevHicon != IntPtr.Zero) DestroyIcon(prevHicon);
                prevHicon = h;
            }
        }

        void ExitApp()
        {
            notify.Visible = false;
            engine.Dispose();
            if (prevHicon != IntPtr.Zero) DestroyIcon(prevHicon);
            ExitThread();
        }
    }
}

