import AppKit
import AVFoundation
import CoreVideo
import Metal

/// The "thank you for 500 downloads" film, made with the app's own engine. A messy wooden floor:
/// the robot eats the lot, empties itself, and only the folders come back out, spelling 500.
///   RoboDust --thanks-video out.mp4 [--frames folder-for-review-stills]
enum ThanksVideo {
    static let view = V2(1600, 900)
    static let W = 1920, H = 1080
    static let px = CGFloat(1.2)
    static let messSize = 130

    static let font: [Character: [String]] = [
        "5": ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        "0": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    ]

    /// Where the folders land, centred on the floor above the dock.
    static func digitSlots() -> [V2] {
        let pitch: Float = 60, top: Float = 118
        let left = (view.x - pitch * 16) / 2
        var out: [V2] = []
        for (d, ch) in "500".enumerated() {
            for col in 0..<5 {
                for (row, line) in font[ch]!.enumerated() where Array(line)[col] == "1" {
                    out.append(V2(left + (Float(d) * 6 + Float(col)) * pitch, top + Float(row) * pitch))
                }
            }
        }
        return out
    }

    static func entries() -> [DesktopEntry] {
        var rng = SeededRNG(seed: 500)
        func r(_ a: Float, _ b: Float) -> Float { a + (b - a) * Float.random(in: 0...1, using: &rng) }
        var out: [DesktopEntry] = []
        func clearOfDock(_ c: V2) -> Bool { !(abs(c.x - 800) < 230 && c.y > 590) }
        func spot() -> V2 { while true { let c = V2(r(40, view.x - 40), r(50, view.y - 40)); if clearOfDock(c) { return c } } }
        // The folders that make the 500 start out as part of the mess, like everything else.
        for _ in digitSlots() {
            out.append(DesktopEntry(name: "", center: spot(), bytes: 40_000_000, kind: .folder, url: nil, image: nil))
        }
        let specs: [(String, Kind, Int64)] = [
            ("Screenshot 2026-10-%d.png", .image, 900_000), ("final-final-v%d.pdf", .document, 300_000), ("notes copy %d.txt", .document, 2_000),
            ("IMG_%d.jpeg", .image, 3_000_000), ("to-delete-later %d", .folder, 2_000_000), ("misc %d", .folder, 1_000_000),
            ("Untitled %d.txt", .document, 40), ("old-site-%d.zip", .archive, 9_000_000), ("voice-memo-%d.m4a", .audio, 3_000_000),
            ("Screen Recording %d.mov", .video, 12_000_000),
        ]
        var i = 0
        while out.count < messSize {
            let s = specs[i % specs.count]
            var e = DesktopEntry(name: String(format: s.0, 10 + i), center: V2(r(40, view.x - 40), r(50, view.y - 40)), bytes: s.2, kind: s.1, url: nil, image: nil)
            if s.1 == .image { e.image = SpriteFactory.sampleThumbnail(seed: i, tall: s.0.hasPrefix("Screenshot")) }
            // A few monsters, so the robot visibly struggles once or twice.
            if i == 17 || i == 54 { e.bytes = 8_000_000_000; e.name = i == 17 ? "backup-of-the-backup.zip" : "weekend-take-12.mov"; e.kind = i == 17 ? .archive : .video }
            if clearOfDock(e.center) { out.append(e) }
            i += 1
        }
        return out
    }

    static func floor() -> CGImage {
        let w = W * 2, h = H * 2
        let ctx = SpriteFactory.context(w: w, h: h, space: CGColorSpace(name: CGColorSpace.sRGB)!)
        var rng = SeededRNG(seed: 12)
        func r(_ a: CGFloat, _ b: CGFloat) -> CGFloat { CGFloat.random(in: a...b, using: &rng) }
        func c(_ cr: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor { CGColor(srgbRed: cr, green: g, blue: b, alpha: a) }
        let plank: CGFloat = 200
        var y: CGFloat = -30
        while y < CGFloat(h) {
            var x = -r(0, 700)
            while x < CGFloat(w) {
                let pw = r(800, 1250), t = r(-0.018, 0.018)
                ctx.setFillColor(c(0.895 + t, 0.775 + t, 0.60 + t)); ctx.fill(CGRect(x: x, y: y, width: pw, height: plank))
                for _ in 0..<9 {
                    let gy = y + r(8, plank - 8)
                    ctx.setStrokeColor(c(0.55, 0.38, 0.2, r(0.025, 0.07))); ctx.setLineWidth(r(1, 2.4))
                    ctx.move(to: CGPoint(x: x, y: gy))
                    ctx.addCurve(to: CGPoint(x: x + pw, y: gy + r(-8, 8)), control1: CGPoint(x: x + pw * 0.3, y: gy + r(-12, 12)), control2: CGPoint(x: x + pw * 0.7, y: gy + r(-12, 12)))
                    ctx.strokePath()
                }
                ctx.setFillColor(c(0.42, 0.28, 0.15, 0.32)); ctx.fill(CGRect(x: x + pw - 2, y: y, width: 2, height: plank))
                x += pw
            }
            ctx.setFillColor(c(0.42, 0.28, 0.15, 0.32)); ctx.fill(CGRect(x: 0, y: y + plank - 2, width: CGFloat(w), height: 2))
            y += plank
        }
        for _ in 0..<3600 {
            let d = r(1.6, 5.4)
            ctx.setFillColor(c(0.36, 0.32, 0.28, r(0.14, 0.4)))
            ctx.fillEllipse(in: CGRect(x: r(0, CGFloat(w)), y: r(0, CGFloat(h)), width: d, height: d))
        }
        return ctx.makeImage()!
    }

    // MARK: - Words on top of the picture

    struct Caption { var text: String; var start: Float; var end: Float }

    static func ease(_ x: Float) -> CGFloat { CGFloat(smooth(0, 1, x)) }

    static func drawCaption(_ cap: Caption, t: Float, in ctx: CGContext) {
        let a = ease((t - cap.start) / 0.3) * ease((cap.end - t) / 0.3)
        guard a > 0 else { return }
        let rise = (1 - ease((t - cap.start) / 0.35)) * 10
        let s = NSAttributedString(string: cap.text, attributes: [
            .font: NSFont.systemFont(ofSize: 34, weight: .semibold),
            .foregroundColor: NSColor(white: 0.07, alpha: a)])
        let sz = s.size()
        let box = CGRect(x: (CGFloat(W) - sz.width) / 2 - 34, y: 34 + rise, width: sz.width + 68, height: sz.height + 28)
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 26, color: CGColor(gray: 0, alpha: 0.28 * a))
        ctx.setFillColor(CGColor(gray: 0.99, alpha: 0.96 * a))
        ctx.addPath(CGPath(roundedRect: box, cornerWidth: box.height / 2, cornerHeight: box.height / 2, transform: nil)); ctx.fillPath()
        ctx.restoreGState()
        s.draw(at: NSPoint(x: box.minX + 34, y: box.minY + 14))
    }

    static func drawCard(_ a: CGFloat, t: Float, icon: NSImage, in ctx: CGContext) {
        guard a > 0 else { return }
        ctx.setFillColor(CGColor(gray: 1, alpha: a)); ctx.fill(CGRect(x: 0, y: 0, width: W, height: H))
        let para = NSMutableParagraphStyle(); para.alignment = .center
        func stage(_ d: Float) -> CGFloat { a * ease((t - d) / 0.35) }
        func lift(_ d: Float) -> CGFloat { (1 - ease((t - d) / 0.45)) * 18 }
        icon.draw(in: CGRect(x: CGFloat(W) / 2 - 90, y: 222 + lift(0), width: 180, height: 180), from: .zero, operation: .sourceOver, fraction: stage(0), respectFlipped: true, hints: nil)
        let ink = NSColor(white: 0.07, alpha: stage(0.12)), grey = NSColor(srgbRed: 0.55, green: 0.55, blue: 0.54, alpha: stage(0.5))
        NSAttributedString(string: "500 downloads. Thank you!", attributes: [.font: NSFont.systemFont(ofSize: 76, weight: .semibold), .kern: -1.6, .foregroundColor: ink, .paragraphStyle: para])
            .draw(in: CGRect(x: 0, y: 450 + lift(0.12), width: CGFloat(W), height: 100))
        NSAttributedString(string: "For trusting a tiny robot with your desktop.", attributes: [.font: NSFont.systemFont(ofSize: 40, weight: .medium), .foregroundColor: grey, .paragraphStyle: para])
            .draw(in: CGRect(x: 0, y: 566 + lift(0.5), width: CGFloat(W), height: 60))
        NSAttributedString(string: "robodust.muhraufan.com", attributes: [.font: NSFont.systemFont(ofSize: 30, weight: .medium), .foregroundColor: NSColor(white: 0.07, alpha: stage(1.0)), .paragraphStyle: para])
            .draw(in: CGRect(x: 0, y: 716 + lift(1.0), width: CGFloat(W), height: 44))
    }

    // MARK: - Film

    static func render(to path: String, frames: String?) throws {
        let device = MTLCreateSystemDefaultDevice()!
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let r = try Renderer(device: device, sizePts: view)
        r.setWallpaper(floor(), space: space)
        SpriteFactory.darkLabels = true
        Sim.dockXOverride = 800
        let ents = entries()
        let slots = digitSlots()
        print("files on the floor:", ents.count, "of which folders for the number:", slots.count)
        let sem = DispatchSemaphore(value: 0)
        var sprites: [SpriteFactory.Sprite] = []
        SpriteFactory.build(ents, space: space, progress: { _ in }) { sprites = $0; sem.signal() }
        while sem.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        r.setSprites(sprites, space: space)

        let synth = Synth()
        let s = Sim(size: view, visibleMin: V2(0, 0), visibleMax: view, entries: ents)
        for (i, sp) in sprites.enumerated() { s.items[i].color = sp.average }
        s.audio = synth
        s.speedScale = 2.1
        s.binCapacity = 100_000
        s.emptyCap = 1.7
        s.laneSpacing = 2.5
        s.intakeWidth = 1.4   // sweeps the whole lane as it passes, so nothing needs forcing in later
        let digits = Array(s.items[0..<slots.count])

        let td = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: W, height: H, mipmapped: false)
        td.usage = [.renderTarget, .shaderRead]; td.storageMode = .shared
        let tex = device.makeTexture(descriptor: td)!

        // Picture and sound are written separately and joined at the end, so neither can hold up the other.
        let videoPath = path + ".video.mp4", audioPath = path + ".audio.m4a"
        for f in [path, videoPath, audioPath] { try? FileManager.default.removeItem(atPath: f) }
        let writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: videoPath), fileType: .mp4)
        let vi = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: W, AVVideoHeightKey: H,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 12_000_000],
            AVVideoColorPropertiesKey: [AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2, AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2, AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2],
        ])
        vi.expectsMediaDataInRealTime = false
        let ad = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vi, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferWidthKey as String: W, kCVPixelBufferHeightKey as String: H])
        writer.add(vi); writer.startWriting(); writer.startSession(atSourceTime: .zero)
        var soundL: [Float] = [], soundR: [Float] = []
        func log(_ text: String) { FileHandle.standardError.write((text + "\n").data(using: .utf8)!) }

        let icon = NSImage(contentsOfFile: "site-draft/assets/icon-large.png")!
        var captions: [Caption] = [Caption(text: "500 of you downloaded RoboDust.", start: 0.4, end: 3.6)]
        var frame = 0
        var secondCaption = false
        var doneAt: Float?, formedAt: Float?
        var nextStill: Float = 0
        var lastPhase = s.phase
        let fps: Float = 60
        var ended = false, cardAt: Float?, endAt: Float?

        while !ended {
            let filmT = Float(frame) / fps
            // Quicker through the slow admin, so the film stays snappy.
            let warp: Int
            switch s.phase {
            case .scanning: warp = 5
            case .cleaning, .hunting: warp = 4
            case .returning, .docking: warp = 4
            case .emptying: warp = 3
            default: warp = 2
            }
            for _ in 0..<warp { s.step(1 / 120) }
            s.scanActive = min(s.scanActive, 0.38)   // the full sweep is too strong with 500 files lighting up

            if s.phase != lastPhase { log(String(format: "%5.1fs -> %@ left=%d", filmT, "\(s.phase)", s.remaining)); lastPhase = s.phase }
            if s.phase == .cleaning && !secondCaption && filmT > 3.9 { secondCaption = true; captions.append(Caption(text: "That’s a lot of messy desktops.", start: filmT, end: filmT + 3.2)) }
            // Empty now, the dock gives back only the folders, and they fly out to make the number.
            if s.phase == .done && doneAt == nil {
                doneAt = filmT
                synth.setMotor(level: 0, load: 0)
                synth.setRoar(0.4)
                captions.append(Caption(text: "And when it had tidied up…", start: filmT + 0.1, end: filmT + 2.5))
                s.launch(Array(zip(digits, slots)), from: { _ in s.bagPos }, flight: .toTidy, spread: 1.7)
                s.bagFill = 0.9
            }
            if doneAt != nil && formedAt == nil && digits.allSatisfy({ $0.flight == .none }) {
                formedAt = filmT
                digits.forEach { $0.protected = true }   // now they spring back to their spots after the bow
                synth.setRoar(0)
                // Confetti from the dock, right in the middle.
                for _ in 0..<640 {
                    let a = s.rand(-Float.pi + 0.12, -0.12), sp = s.rand(260, 1500)
                    let palette: [SIMD3<Float>] = [SIMD3(0.04, 0.47, 0.70), SIMD3(0.91, 0.32, 0.24), SIMD3(0.98, 0.76, 0.16), SIMD3(0.20, 0.72, 0.47), SIMD3(0.55, 0.36, 0.86), SIMD3(1, 1, 1)]
                    let c = palette[Int(s.rand(0, 5.99))]
                    s.spawn(s.dockPos + V2(s.rand(-50, 50), -100), V2(cos(a), sin(a)) * sp, life: s.rand(1.4, 3.3), size: s.rand(6, 13), color: SIMD4(c.x, c.y, c.z, 1), chip: 1)
                }
                synth.chirp([(523, 0.1), (659, 0.1), (784, 0.1), (1047, 0.14), (784, 0.09), (1047, 0.09), (1319, 0.34)])
                captions.append(Caption(text: "Thank you.", start: filmT + 0.1, end: filmT + 2.7))
                cardAt = filmT + 2.7
            }
            // The digits take a bow, one column after another.
            if let d = formedAt {
                let e = filmT - d
                for it in digits {
                    let delay = (it.rest.x - 320) / 960 * 0.75 + 0.1
                    if e >= delay && e - 1 / fps < delay { it.icon.vel.y -= 720; it.icon.angV += s.rand(-3, 3) }
                }
            }

            let cb = r.queue.makeCommandBuffer()!
            r.encode(sim: s, target: tex, cb: cb, dt: Float(warp) / 120)
            cb.commit(); cb.waitUntilCompleted()

            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, ad.pixelBufferPool!, &pb)
            guard let buf = pb else { break }
            CVPixelBufferLockBaseAddress(buf, [])
            let base = CVPixelBufferGetBaseAddress(buf)!, row = CVPixelBufferGetBytesPerRow(buf)
            tex.getBytes(base, bytesPerRow: row, from: MTLRegionMake2D(0, 0, W, H), mipmapLevel: 0)
            let ctx = CGContext(data: base, width: W, height: H, bitsPerComponent: 8, bytesPerRow: row, space: space,
                                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)!
            ctx.translateBy(x: 0, y: CGFloat(H)); ctx.scaleBy(x: 1, y: -1)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
            for c in captions where filmT >= c.start && filmT <= c.end { drawCaption(c, t: filmT, in: ctx) }
            if let c = cardAt, filmT >= c {
                let local = filmT - c
                drawCard(ease(local / 0.5), t: local - 0.15, icon: icon, in: ctx)
                if local > 0.55 && endAt == nil {
                    endAt = c + 3.8; synth.setMotor(level: 0, load: 0)
                    synth.chirp([(784, 0.11), (1047, 0.11), (1319, 0.11), (1568, 0.6)])
                }
            }
            NSGraphicsContext.restoreGraphicsState()
            if let dir = frames, filmT >= nextStill {
                nextStill += 1.5
                let img = ctx.makeImage()!
                let flipped = SpriteFactory.context(w: W, h: H, space: space)
                flipped.draw(img, in: CGRect(x: 0, y: 0, width: W, height: H))
                let rep = NSBitmapImageRep(cgImage: flipped.makeImage()!)
                try rep.representation(using: .jpeg, properties: [.compressionFactor: 0.8])!.write(to: URL(fileURLWithPath: dir + String(format: "/t%05.1f.jpg", filmT)))
            }
            CVPixelBufferUnlockBaseAddress(buf, [])
            while !vi.isReadyForMoreMediaData {
                if writer.status == .failed { throw writer.error ?? NSError(domain: "RoboDust", code: 2) }
                RunLoop.main.run(until: Date().addingTimeInterval(0.002))
            }
            ad.append(buf, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 60))
            if frame % 60 == 0 { log(String(format: "film %.0fs  phase %@  left %d", filmT, "\(s.phase)", s.remaining)) }

            let n = 800
            var L = [Float](repeating: 0, count: n), R = [Float](repeating: 0, count: n)
            synth.render(n, &L, &R)
            // A soft fade so the sound doesn't end abruptly.
            let fade: Float = endAt.map { clampf(($0 - filmT) / 0.6, 0, 1) } ?? 1
            soundL.append(contentsOf: L.map { $0 * fade * 0.8 }); soundR.append(contentsOf: R.map { $0 * fade * 0.8 })

            frame += 1
            if let e = endAt, filmT >= e { ended = true }
            if filmT > 75 { print("safety stop"); ended = true }
        }
        vi.markAsFinished()
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        guard writer.status == .completed else { throw writer.error ?? NSError(domain: "RoboDust", code: 3) }

        // Sound.
        let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
        do {
            let file = try AVAudioFile(forWriting: URL(fileURLWithPath: audioPath), settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192000])
            let pcm = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(soundL.count))!
            pcm.frameLength = AVAudioFrameCount(soundL.count)
            soundL.withUnsafeBufferPointer { pcm.floatChannelData![0].update(from: $0.baseAddress!, count: soundL.count) }
            soundR.withUnsafeBufferPointer { pcm.floatChannelData![1].update(from: $0.baseAddress!, count: soundR.count) }
            try file.write(from: pcm)
        }

        // Join them.
        let comp = AVMutableComposition()
        let vAsset = AVURLAsset(url: URL(fileURLWithPath: videoPath)), aAsset = AVURLAsset(url: URL(fileURLWithPath: audioPath))
        let duration = vAsset.duration
        try comp.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)!
            .insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: vAsset.tracks(withMediaType: .video)[0], at: .zero)
        let audioRange = CMTimeRange(start: .zero, duration: min(duration, aAsset.duration))
        try comp.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)!
            .insertTimeRange(audioRange, of: aAsset.tracks(withMediaType: .audio)[0], at: .zero)
        let export = AVAssetExportSession(asset: comp, presetName: AVAssetExportPresetPassthrough)!
        export.outputURL = URL(fileURLWithPath: path); export.outputFileType = .mp4
        let joined = DispatchSemaphore(value: 0)
        export.exportAsynchronously { joined.signal() }
        while joined.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        if export.status != .completed { throw export.error ?? NSError(domain: "RoboDust", code: 4) }
        for f in [videoPath, audioPath] { try? FileManager.default.removeItem(atPath: f) }
        log(String(format: "film %.1fs written", Float(frame) / fps))
    }
}
