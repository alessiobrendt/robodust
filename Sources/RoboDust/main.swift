import AppKit

let args = CommandLine.arguments
func value(_ flag: String) -> String? {
    guard let i = args.firstIndex(of: flag), i + 1 < args.count else { return nil }
    return args[i + 1]
}

if args.contains("--dump") || args.contains("--video") {
    _ = NSApplication.shared
    var o = Offline.Options()
    if let d = value("--dump") { o.dumpTimes = d.split(separator: ",").compactMap { Float($0) } }
    if let d = value("--out") { o.dumpDir = d }
    o.videoPath = value("--video")
    if let m = value("--seconds"), let f = Float(m) { o.maxSeconds = f }
    if let m = value("--mode"), let v = Int(m), let c = CleanMode(rawValue: v) { o.mode = c }
    o.skip = args.contains("--skip")
    if let m = value("--choose"), let v = Int(m), let c = CleanMode(rawValue: v) { o.choose = c }
    if let m = value("--count"), let v = Int(m) { o.count = v }
    if let c = value("--crop") {
        let n = c.split(separator: ",").compactMap { Double($0) }
        if n.count == 4 { o.crop = CGRect(x: n[0], y: n[1], width: n[2], height: n[3]) }
    }
    o.plainFloor = args.contains("--plain-floor")
    if let m = value("--cat"), let v = Int(m) { o.catMood = CatActor.Mood(rawValue: v) }
    if let m = value("--cat-pick"), let v = Float(m) { o.catPickAt = v }
    o.catDropOnRobot = args.contains("--cat-on-robot")
    if let s = value("--far-pull"), let f = Float(s) { o.farPullAt = f }
    if let s = value("--follow"), let f = Int(s) { o.follow = f }
    if let s = value("--pixel-scale"), let f = Float(s) { o.pixelScale = f }
    try Offline.run(o)
    exit(0)
}

if let dir = value("--cat-test") {
    _ = NSApplication.shared
    try CatTest.stills(to: dir)
    try CatTest.clip(to: dir + "/cat-style-test.mp4")
    try CatTest.clip(to: dir + "/cat-walk-closeup.mp4", closeUp: true)
    exit(0)
}

if let path = value("--cat-sounds") {
    _ = NSApplication.shared
    try CatTest.soundSampler(to: path)
    exit(0)
}

if let dir = value("--cat-fight") {
    _ = NSApplication.shared
    try CatTest.fightClip(to: dir + "/mood-swatter-closeup.mp4")
    try CatTest.moodClip(.swatter, seconds: 30, to: dir + "/mood-swatter.mp4")
    exit(0)
}

if let dir = value("--cat-drops") {
    _ = NSApplication.shared
    try CatTest.moodClip(.loaf, seconds: 24, to: dir + "/drop-loaf.mp4", pickAt: 11, onRobot: true)
    try CatTest.moodClip(.swatter, seconds: 20, to: dir + "/drop-swatter.mp4", pickAt: 9, onRobot: true)
    exit(0)
}

if let dir = value("--cat-moods") {
    _ = NSApplication.shared
    try CatTest.moodClip(.supervisor, seconds: 18, to: dir + "/mood-supervisor.mp4", pickAt: 9)
    try CatTest.moodClip(.loaf, seconds: 24, to: dir + "/mood-loaf.mp4")
    try CatTest.moodClip(.rider, seconds: 34, to: dir + "/mood-rider.mp4")
    try CatTest.moodClip(.saboteur, seconds: 24, to: dir + "/mood-saboteur.mp4")
    try CatTest.moodClip(.coward, seconds: 40, to: dir + "/mood-coward.mp4")
    exit(0)
}

if let path = value("--thanks-video") {
    _ = NSApplication.shared
    try ThanksVideo.render(to: path, frames: value("--frames"))
    exit(0)
}

if let dir = value("--export-art") {
    _ = NSApplication.shared
    try Offline.exportArt(to: dir)
    exit(0)
}

if let dir = value("--fileops-test") {
    _ = NSApplication.shared
    let ok = FileOps.selfTest(sandbox: URL(fileURLWithPath: dir))
    print(ok ? "ALL PASSED" : "SOME FAILED")
    exit(ok ? 0 : 1)
}

if args.contains("--icloud-test") {
    _ = NSApplication.shared
    let ok = FileOps.iCloudTest()
    print(ok ? "ALL PASSED" : "SOME FAILED")
    exit(ok ? 0 : 1)
}

if args.contains("--read-test") {
    // Exercises the real-desktop path, printing only counts: no names, no pixels.
    _ = NSApplication.shared
    let screen = NSScreen.screens.first!
    let t0 = CFAbsoluteTimeGetCurrent()
    let e = try DesktopReader.read(on: screen)
    print("items", e.count, String(format: "finder %.2fs", CFAbsoluteTimeGetCurrent() - t0))
    var byKind: [String: Int] = [:]
    e.forEach { byKind[$0.kind.title, default: 0] += 1 }
    print(byKind.sorted { $0.key < $1.key })
    print("left alone", e.filter { $0.protectedReason != nil }.count, "not on disk", e.filter { $0.missing }.count, "icloud", e.filter { $0.iCloud }.count)
    print("total GB", String(format: "%.1f", Double(e.reduce(0) { $0 + $1.bytes }) / 1e9))
    let done = DispatchSemaphore(value: 0)
    let t1 = CFAbsoluteTimeGetCurrent()
    var count = 0
    SpriteFactory.build(e, space: screen.colorSpace!.cgColorSpace!, progress: { _ in }) { s in count = s.count; done.signal() }
    while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
    print("sprites", count, String(format: "%.2fs", CFAbsoluteTimeGetCurrent() - t1))
    exit(0)
}

if let out = value("--panel-shot") {
    _ = NSApplication.shared
    PanelShot.render(to: out)
    exit(0)
}

let app = NSApplication.shared
let controller = AppController()
if args.contains("--no-hide") { controller.model.clearWindows = false }
if args.contains("--auto") { controller.autoDemo = true }
controller.updateTestLog = value("--update-test")
if args.contains("--auto-putback") { controller.autoPutBack = true }
controller.telemetryPath = value("--telemetry")
if args.contains("--no-save") { Model.persists = false }
// Dev only: always the same mood, to check one at a time. 0 Supervisor, 1 Loaf, 2 Rider, 3 Swatter.
if let m = value("--cat"), let v = Int(m), let mood = CatActor.Mood(rawValue: v) {
    controller.model.hasCat = true
    controller.forcedCatMood = mood
}
if let root = value("--test-root") {
    // Dev only: a throwaway folder stands in for the desktop, and settings aren't saved.
    Model.persists = false
    FileOps.historyEnabled = false
    FileOps.mountHidden = true
    controller.testRoot = URL(fileURLWithPath: root)
    controller.model.source = .real
    if let m = value("--test-mode"), let v = Int(m), let c = CleanMode(rawValue: v) { controller.model.mode = c }
    controller.model.skipFinalClick = true
}
app.delegate = controller
app.run()
