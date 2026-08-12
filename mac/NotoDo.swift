// NotoDo - Notionと双方向同期するデスクトップTODOウィジェット (macOS版)
// メニューバー常駐 + フローティングウィジェット。
// 設定: ~/Library/Application Support/NotoDo/notion-config.json に Token / DatabaseId を記述。
import Cocoa

// ===== データ =====

final class TodoItem {
    var notionId: String?
    var text: String
    var done = false
    var doneAt: String?   // 完了日 "yyyy-MM-dd"（未完了は nil）
    var hidden = false    // Notionの「デスクトップ非表示」チェック
    init(text: String) { self.text = text }
}

final class TodoStore {
    var items: [TodoItem] = []
    var onChange: (() -> Void)?
    let dir: URL
    private let path: URL

    init() {
        dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("NotoDo")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        path = dir.appendingPathComponent("todos.json")
        load()
    }

    static func today() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: Date())
    }

    func fire() { onChange?() }

    func remainCount() -> Int { items.filter { !$0.done && !$0.hidden }.count }

    // 表示対象: 未完了すべて + 今日完了した項目。新しい順、完了済みは最下部。
    func visibleItems() -> [TodoItem] {
        let today = TodoStore.today()
        let vis = items
            .filter { !$0.hidden }
            .filter { !$0.done || $0.doneAt == nil || $0.doneAt! >= today }
        return Array(vis.reversed()).sorted { a, b in !a.done && b.done }
    }

    private func load() {
        guard let data = try? Data(contentsOf: path),
              let arr = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] else { return }
        for d in arr {
            guard let text = d["Text"] as? String else { continue }
            let it = TodoItem(text: text)
            it.done = d["Done"] as? Bool ?? false
            it.notionId = d["Id"] as? String
            it.doneAt = d["DoneAt"] as? String
            it.hidden = d["Hidden"] as? Bool ?? false
            items.append(it)
        }
    }

    func save() {
        let arr: [[String: Any]] = items.map {
            var d: [String: Any] = ["Text": $0.text, "Done": $0.done, "Hidden": $0.hidden]
            if let id = $0.notionId { d["Id"] = id }
            if let da = $0.doneAt { d["DoneAt"] = da }
            return d
        }
        if let data = try? JSONSerialization.data(withJSONObject: arr) {
            try? data.write(to: path)
        }
    }
}

// ===== Notion同期 =====

enum SyncStatus { case noConfig, ok, syncing, error }

final class SyncEngine {
    private struct PushOp {
        let item: TodoItem
        let id: String?
        let text: String
        let done: Bool
        let doneAt: String?
        let hidden: Bool
    }
    private struct RemoteItem {
        let id: String
        let text: String
        let done: Bool
        let doneAt: String?
        let lastEdited: String?
        let hidden: Bool
    }

    private let store: TodoStore
    private let configPath: URL
    private let signal = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var ops: [ObjectIdentifier: PushOp] = [:]
    private var archives: [String] = []
    private var stopped = false
    private var forcePoll = true
    private var lastPoll = Date.distantPast
    private var errorUntil = Date.distantPast

    private var token = ""
    private var dbId = ""
    private var cfgTime: Date?
    private var titleProp: String?
    private var doneProp: String?
    private var dateProp: String?
    private var hideProp: String?

    private(set) var status: SyncStatus = .noConfig
    private(set) var statusDetail = ""
    private(set) var lastOk: Date?
    var onStatusChange: (() -> Void)?
    var databaseId: String { dbId }

    private let pollIntervalSec: TimeInterval = 60

    init(store: TodoStore) {
        self.store = store
        configPath = store.dir.appendingPathComponent("notion-config.json")
    }

    func start() {
        let t = Thread { self.loop() }
        t.name = "NotoDoSync"
        t.start()
    }

    func stop() {
        stopped = true
        signal.signal()
    }

    func notifyChanged(_ item: TodoItem) {
        lock.lock()
        ops[ObjectIdentifier(item)] = PushOp(item: item, id: item.notionId, text: item.text,
                                             done: item.done, doneAt: item.doneAt, hidden: item.hidden)
        lock.unlock()
        signal.signal()
    }

    func notifyDeleted(_ item: TodoItem) {
        lock.lock()
        ops.removeValue(forKey: ObjectIdentifier(item))
        if let id = item.notionId { archives.append(id) }
        lock.unlock()
        signal.signal()
    }

    func requestSync() {
        forcePoll = true
        errorUntil = .distantPast
        signal.signal()
    }

    private func setStatus(_ st: SyncStatus, _ detail: String) {
        status = st
        statusDetail = detail
        if st == .ok { lastOk = Date() }
        DispatchQueue.main.async { self.onStatusChange?() }
    }

    private func loop() {
        while !stopped {
            _ = signal.wait(timeout: .now() + 2)
            if stopped { break }
            do {
                loadConfig()
                if token.isEmpty || dbId.isEmpty {
                    setStatus(.noConfig, "notion-config.json の Token が未設定です")
                    continue
                }
                if Date() < errorUntil { continue }
                if titleProp == nil { try loadSchema() }

                let pushed = try processQueue()
                if pushed { forcePoll = true }

                lock.lock()
                let hasPending = !ops.isEmpty || !archives.isEmpty
                lock.unlock()

                if !hasPending && (forcePoll || Date().timeIntervalSince(lastPoll) >= pollIntervalSec) {
                    forcePoll = false
                    let remote = try queryAll()
                    lastPoll = Date()
                    DispatchQueue.main.sync { self.applyRemote(remote) }
                    setStatus(.ok, "")
                }
            } catch {
                errorUntil = Date().addingTimeInterval(30)
                titleProp = nil  // スキーマ変更が原因の可能性もあるので次回再取得
                setStatus(.error, error.localizedDescription)
            }
        }
    }

    private func processQueue() throws -> Bool {
        lock.lock()
        let batch = Array(ops.values)
        ops.removeAll()
        let arch = archives
        archives.removeAll()
        lock.unlock()
        if batch.isEmpty && arch.isEmpty { return false }
        setStatus(.syncing, "")

        for id in arch {
            do { try archivePage(id) }
            catch {
                lock.lock(); archives.append(id); lock.unlock()
                throw error
            }
        }
        for op in batch {
            do {
                if let id = op.id {
                    try updatePage(id, op)
                } else {
                    let newId = try createPage(op)
                    DispatchQueue.main.async {
                        op.item.notionId = newId
                        self.store.save()
                    }
                }
            } catch {
                // 失敗した操作は残す（その間に新しい操作が積まれていたらそちらを優先）
                lock.lock()
                let key = ObjectIdentifier(op.item)
                if ops[key] == nil { ops[key] = op }
                lock.unlock()
                throw error
            }
        }
        return true
    }

    // メインスレッドで実行。リモートの状態をローカルへマージする。
    private func applyRemote(_ remote: [RemoteItem]) {
        lock.lock()
        let pending = !ops.isEmpty || !archives.isEmpty
        lock.unlock()
        if pending { return }  // ローカル変更が滞留している間は見送り

        var newList: [TodoItem] = []
        var rest = store.items
        for r in remote {
            var local = rest.first { $0.notionId == r.id }
            if local == nil { local = rest.first { $0.notionId == nil && $0.text == r.text } }
            if let l = local {
                rest.removeAll { $0 === l }
                l.notionId = r.id
                l.text = r.text
                l.done = r.done
                l.doneAt = r.doneAt
                l.hidden = r.hidden
                fixDoneAt(l, r)
                newList.append(l)
            } else {
                let it = TodoItem(text: r.text)
                it.notionId = r.id
                it.done = r.done
                it.doneAt = r.doneAt
                it.hidden = r.hidden
                fixDoneAt(it, r)
                newList.append(it)
            }
        }
        for l in rest where l.notionId == nil {
            // ローカルにしか無い項目 → Notionへアップロード
            newList.append(l)
            notifyChanged(l)
        }
        // notionId有りでリモートに無い項目 → リモートで削除されたので破棄
        store.items = newList
        store.save()
        store.fire()
    }

    // 「完了⇔完了日」の整合を取る。スマホ等でチェックだけ変えた場合に補完・クリアする。
    private func fixDoneAt(_ item: TodoItem, _ r: RemoteItem) {
        if item.done && item.doneAt == nil {
            item.doneAt = Self.lastEditedDate(r.lastEdited)
            notifyChanged(item)
        } else if !item.done && item.doneAt != nil {
            item.doneAt = nil
            notifyChanged(item)
        }
    }

    private static func lastEditedDate(_ iso: String?) -> String {
        if let iso = iso {
            let f = ISO8601DateFormatter()
            f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            var d = f.date(from: iso)
            if d == nil {
                f.formatOptions = [.withInternetDateTime]
                d = f.date(from: iso)
            }
            if let d = d {
                let df = DateFormatter()
                df.dateFormat = "yyyy-MM-dd"
                return df.string(from: d)
            }
        }
        return TodoStore.today()
    }

    // ---- 設定・スキーマ ----

    private func loadConfig() {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: configPath.path),
              let mtime = attrs[.modificationDate] as? Date else {
            token = ""
            return
        }
        if mtime == cfgTime { return }
        cfgTime = mtime
        titleProp = nil
        token = ""
        dbId = ""
        guard let data = try? Data(contentsOf: configPath),
              let d = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }
        token = (d["Token"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        dbId = (d["DatabaseId"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "-", with: "")
    }

    private func loadSchema() throws {
        let resp = try request("GET", "https://api.notion.com/v1/databases/\(dbId)", nil)
        titleProp = "Name"
        doneProp = nil
        dateProp = nil
        hideProp = nil
        var checkboxes: [String] = []
        if let props = resp["properties"] as? [String: Any] {
            for (name, v) in props {
                guard let pd = v as? [String: Any], let ty = pd["type"] as? String else { continue }
                if ty == "title" { titleProp = name }
                else if ty == "checkbox" { checkboxes.append(name) }
                else if ty == "date" && dateProp == nil { dateProp = name }
            }
        }
        // チェックボックスが複数あるため名前で役割を割り当てる
        doneProp = checkboxes.contains("Done") ? "Done" : checkboxes.first
        hideProp = checkboxes.contains("デスクトップ非表示") ? "デスクトップ非表示"
            : checkboxes.first { $0 != doneProp }
    }

    // ---- Notion API ----

    private func queryAll() throws -> [RemoteItem] {
        var result: [RemoteItem] = []
        var cursor: String?
        repeat {
            var body: [String: Any] = [
                "page_size": 100,
                "sorts": [["timestamp": "created_time", "direction": "ascending"]]
            ]
            if let c = cursor { body["start_cursor"] = c }
            let resp = try request("POST", "https://api.notion.com/v1/databases/\(dbId)/query",
                                   try JSONSerialization.data(withJSONObject: body))
            let results = resp["results"] as? [[String: Any]] ?? []
            for page in results {
                guard let id = page["id"] as? String,
                      let props = page["properties"] as? [String: Any] else { continue }
                var text = ""
                if let tp = titleProp, let td = props[tp] as? [String: Any],
                   let arr = td["title"] as? [[String: Any]] {
                    text = arr.compactMap { $0["plain_text"] as? String }.joined()
                }
                var done = false
                if let dp = doneProp, let dd = props[dp] as? [String: Any] {
                    done = dd["checkbox"] as? Bool ?? false
                }
                var hidden = false
                if let hp = hideProp, let hd = props[hp] as? [String: Any] {
                    hidden = hd["checkbox"] as? Bool ?? false
                }
                var doneAt: String?
                if let pp = dateProp, let pd = props[pp] as? [String: Any],
                   let dv = pd["date"] as? [String: Any], let s = dv["start"] as? String {
                    doneAt = String(s.prefix(10))
                }
                let lastEdited = page["last_edited_time"] as? String
                if !text.isEmpty {
                    result.append(RemoteItem(id: id, text: text, done: done,
                                             doneAt: doneAt, lastEdited: lastEdited, hidden: hidden))
                }
            }
            cursor = (resp["has_more"] as? Bool ?? false) ? resp["next_cursor"] as? String : nil
        } while cursor != nil
        return result
    }

    private func buildProps(_ op: PushOp) -> [String: Any] {
        var props: [String: Any] = [:]
        props[titleProp ?? "Name"] = ["title": [["text": ["content": op.text]]]]
        if let dp = doneProp { props[dp] = ["checkbox": op.done] }
        if let pp = dateProp {
            if let da = op.doneAt {
                props[pp] = ["date": ["start": da]]
            } else {
                props[pp] = ["date": NSNull()]
            }
        }
        if let hp = hideProp { props[hp] = ["checkbox": op.hidden] }
        return props
    }

    private func createPage(_ op: PushOp) throws -> String {
        let body: [String: Any] = [
            "parent": ["database_id": dbId],
            "properties": buildProps(op)
        ]
        let resp = try request("POST", "https://api.notion.com/v1/pages",
                               try JSONSerialization.data(withJSONObject: body))
        guard let id = resp["id"] as? String else {
            throw NSError(domain: "NotoDo", code: 0,
                          userInfo: [NSLocalizedDescriptionKey: "作成レスポンスにidがありません"])
        }
        return id
    }

    private func updatePage(_ id: String, _ op: PushOp) throws {
        let body: [String: Any] = ["properties": buildProps(op)]
        _ = try request("PATCH", "https://api.notion.com/v1/pages/\(id)",
                        try JSONSerialization.data(withJSONObject: body))
    }

    private func archivePage(_ id: String) throws {
        _ = try request("PATCH", "https://api.notion.com/v1/pages/\(id)",
                        "{\"archived\":true}".data(using: .utf8))
    }

    private func request(_ method: String, _ urlStr: String, _ body: Data?) throws -> [String: Any] {
        guard let url = URL(string: urlStr) else {
            throw NSError(domain: "NotoDo", code: 0, userInfo: [NSLocalizedDescriptionKey: "bad url"])
        }
        var req = URLRequest(url: url)
        req.httpMethod = method
        req.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        req.setValue("2022-06-28", forHTTPHeaderField: "Notion-Version")
        req.setValue("NotoDo-mac/1.0", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 15
        if let b = body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = b
        }
        var resultData: Data?
        var resultErr: Error?
        var statusCode = 0
        let sem = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: req) { data, resp, err in
            resultData = data
            resultErr = err
            statusCode = (resp as? HTTPURLResponse)?.statusCode ?? 0
            sem.signal()
        }.resume()
        sem.wait()
        if let e = resultErr { throw e }
        let obj = (resultData.flatMap { try? JSONSerialization.jsonObject(with: $0) }) as? [String: Any] ?? [:]
        if statusCode >= 400 {
            let msg = obj["message"] as? String ?? "HTTP \(statusCode)"
            throw NSError(domain: "NotoDo", code: statusCode,
                          userInfo: [NSLocalizedDescriptionKey: "\(statusCode): \(msg)"])
        }
        return obj
    }
}

// ===== UI =====

final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

final class WidgetPanel {
    private let panel: NSPanel
    private let container = FlippedView()
    private let store: TodoStore
    private let engine: SyncEngine
    private var view: [TodoItem] = []
    private let statusDot = NSView()

    private let width: CGFloat = 260
    private let headerH: CGFloat = 32
    private let rowH: CGFloat = 27
    private let addH: CGFloat = 28
    private let padBottom: CGFloat = 6

    var isVisible: Bool { panel.isVisible }

    init(store: TodoStore, engine: SyncEngine) {
        self.store = store
        self.engine = engine
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: width, height: 300),
                        styleMask: [.nonactivatingPanel, .borderless],
                        backing: .buffered, defer: false)
        panel.level = .floating
        panel.isMovableByWindowBackground = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces]

        let root = NSView()
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor(red: 0.125, green: 0.133, blue: 0.157, alpha: 1).cgColor
        root.layer?.cornerRadius = 10
        panel.contentView = root

        container.frame = root.bounds
        container.autoresizingMask = [.width, .height]
        root.addSubview(container)

        if !panel.setFrameUsingName("NotoDoPanel") {
            if let screen = NSScreen.main {
                let f = screen.visibleFrame
                panel.setFrameOrigin(NSPoint(x: f.maxX - width - 24, y: f.maxY - 500))
            }
        }
        panel.setFrameAutosaveName("NotoDoPanel")
        rebuild()
    }

    func show() { panel.orderFrontRegardless() }
    func hide() { panel.orderOut(nil) }
    func toggle() { if panel.isVisible { hide() } else { show() } }

    func rebuild() {
        view = store.visibleItems()
        container.subviews.forEach { $0.removeFromSuperview() }

        // ヘッダ
        let title = NSTextField(labelWithString: "TODO")
        title.font = NSFont.boldSystemFont(ofSize: 12)
        title.textColor = .white
        title.frame = NSRect(x: 12, y: 8, width: 80, height: 16)
        container.addSubview(title)

        let df = DateFormatter()
        df.locale = Locale(identifier: "ja_JP")
        df.dateFormat = "M/d (E)"
        let dateLabel = NSTextField(labelWithString: df.string(from: Date()))
        dateLabel.font = NSFont.systemFont(ofSize: 10)
        dateLabel.textColor = NSColor(white: 0.62, alpha: 1)
        dateLabel.alignment = .right
        dateLabel.frame = NSRect(x: width - 100 - 26, y: 10, width: 100, height: 14)
        container.addSubview(dateLabel)

        statusDot.wantsLayer = true
        statusDot.frame = NSRect(x: width - 18, y: 12, width: 8, height: 8)
        statusDot.layer?.cornerRadius = 4
        container.addSubview(statusDot)
        updateStatusDot()

        let line = NSBox()
        line.boxType = .separator
        line.frame = NSRect(x: 10, y: headerH - 2, width: width - 20, height: 1)
        container.addSubview(line)

        // 行
        var y = headerH
        for (i, item) in view.enumerated() {
            let check = NSButton(checkboxWithTitle: "", target: self, action: #selector(toggleRow(_:)))
            check.state = item.done ? .on : .off
            check.tag = i
            check.frame = NSRect(x: 12, y: y + 5, width: 18, height: 18)
            container.addSubview(check)

            let label = NSTextField(labelWithString: item.text)
            if item.done {
                label.attributedStringValue = NSAttributedString(
                    string: item.text,
                    attributes: [
                        .strikethroughStyle: NSUnderlineStyle.single.rawValue,
                        .foregroundColor: NSColor(white: 0.5, alpha: 1),
                        .font: NSFont.systemFont(ofSize: 12)
                    ])
            } else {
                label.textColor = NSColor(white: 0.87, alpha: 1)
                label.font = NSFont.systemFont(ofSize: 12)
            }
            label.lineBreakMode = .byTruncatingTail
            label.frame = NSRect(x: 36, y: y + 6, width: width - 36 - 28, height: 16)
            label.toolTip = item.text
            label.tag = i
            let dbl = NSClickGestureRecognizer(target: self, action: #selector(editRow(_:)))
            dbl.numberOfClicksRequired = 2
            label.addGestureRecognizer(dbl)
            container.addSubview(label)

            let del = NSButton(title: "×", target: self, action: #selector(deleteRow(_:)))
            del.isBordered = false
            del.font = NSFont.systemFont(ofSize: 13)
            del.contentTintColor = NSColor(white: 0.45, alpha: 1)
            del.tag = i
            del.frame = NSRect(x: width - 26, y: y + 4, width: 20, height: 20)
            container.addSubview(del)

            y += rowH
        }

        // 追加行
        let add = NSButton(title: "＋ 追加", target: self, action: #selector(addTask))
        add.isBordered = false
        add.contentTintColor = NSColor(white: 0.55, alpha: 1)
        add.font = NSFont.systemFont(ofSize: 12)
        add.alignment = .left
        add.frame = NSRect(x: 8, y: y + 3, width: 100, height: 20)
        container.addSubview(add)

        // 高さを内容に合わせる（上端固定でリサイズ）
        let newH = headerH + CGFloat(view.count) * rowH + addH + padBottom
        var f = panel.frame
        let topY = f.origin.y + f.size.height
        f.size.width = width
        f.size.height = newH
        f.origin.y = topY - newH
        panel.setFrame(f, display: true)
    }

    func updateStatusDot() {
        // Notion未設定＝ローカルモードではドットを表示しない
        statusDot.isHidden = (engine.status == .noConfig)
        let color: NSColor
        switch engine.status {
        case .ok: color = NSColor(red: 0.33, green: 0.75, blue: 0.51, alpha: 1)
        case .syncing: color = NSColor(red: 0.9, green: 0.71, blue: 0.27, alpha: 1)
        case .error: color = NSColor(red: 0.88, green: 0.37, blue: 0.37, alpha: 1)
        case .noConfig: color = NSColor(white: 0.45, alpha: 1)
        }
        statusDot.layer?.backgroundColor = color.cgColor
        statusDot.toolTip = engine.statusDetail.isEmpty ? nil : engine.statusDetail
    }

    @objc private func toggleRow(_ sender: NSButton) {
        guard sender.tag < view.count else { return }
        let item = view[sender.tag]
        item.done = !item.done
        item.doneAt = item.done ? TodoStore.today() : nil
        store.save()
        store.fire()
        engine.notifyChanged(item)
    }

    @objc private func deleteRow(_ sender: NSButton) {
        guard sender.tag < view.count else { return }
        let item = view[sender.tag]
        store.items.removeAll { $0 === item }
        store.save()
        store.fire()
        engine.notifyDeleted(item)
    }

    @objc private func editRow(_ g: NSClickGestureRecognizer) {
        guard let label = g.view as? NSTextField, label.tag < view.count else { return }
        let item = view[label.tag]
        if let text = Self.prompt(title: "TODOを編集", initial: item.text),
           !text.isEmpty, text != item.text {
            item.text = text
            store.save()
            store.fire()
            engine.notifyChanged(item)
        }
    }

    @objc func addTask() {
        if let text = Self.prompt(title: "TODOを追加", initial: ""), !text.isEmpty {
            let item = TodoItem(text: text)
            store.items.append(item)
            store.save()
            store.fire()
            engine.notifyChanged(item)
        }
    }

    private static func prompt(title: String, initial: String) -> String? {
        let alert = NSAlert()
        alert.messageText = title
        let tf = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        tf.stringValue = initial
        alert.accessoryView = tf
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "キャンセル")
        alert.window.initialFirstResponder = tf
        NSApp.activate(ignoringOtherApps: true)
        let res = alert.runModal()
        if res == .alertFirstButtonReturn {
            return tf.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return nil
    }
}

// ===== アプリ本体 =====

@main
final class AppDelegate: NSObject, NSApplicationDelegate {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }

    private var statusItem: NSStatusItem!
    private var store: TodoStore!
    private var engine: SyncEngine!
    private var widget: WidgetPanel!
    private var menu: NSMenu!
    private var midnightTimer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        store = TodoStore()
        engine = SyncEngine(store: store)
        widget = WidgetPanel(store: store, engine: engine)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusClicked)
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])

        menu = NSMenu()
        menu.addItem(NSMenuItem(title: "今すぐ同期", action: #selector(syncNow), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Notionで開く", action: #selector(openNotion), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "終了", action: #selector(quit), keyEquivalent: "q"))
        for item in menu.items { item.target = self }

        store.onChange = { [weak self] in
            self?.widget.rebuild()
            self?.updateStatusTitle()
        }
        engine.onStatusChange = { [weak self] in
            self?.widget.updateStatusDot()
            self?.updateStatusTitle()
        }

        updateStatusTitle()
        widget.show()
        scheduleMidnight()
        engine.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        engine.stop()
    }

    private func updateStatusTitle() {
        statusItem.button?.title = "✓\(store.remainCount())"
    }

    // 日付が変わったら昨日完了した項目を表示から外す
    private func scheduleMidnight() {
        midnightTimer?.invalidate()
        let cal = Calendar.current
        let nextMidnight = cal.startOfDay(for: cal.date(byAdding: .day, value: 1, to: Date())!)
        let interval = max(nextMidnight.timeIntervalSinceNow + 2, 5)
        midnightTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: false) { [weak self] _ in
            self?.store.fire()
            self?.scheduleMidnight()
        }
    }

    @objc private func statusClicked() {
        if NSApp.currentEvent?.type == .rightMouseUp {
            statusItem.menu = menu
            statusItem.button?.performClick(nil)
            statusItem.menu = nil
        } else {
            widget.toggle()
        }
    }

    @objc private func syncNow() { engine.requestSync() }

    @objc private func openNotion() {
        let db = engine.databaseId
        if !db.isEmpty, let url = URL(string: "https://www.notion.so/\(db)") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func quit() { NSApp.terminate(nil) }
}
