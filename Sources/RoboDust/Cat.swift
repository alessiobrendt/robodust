import AppKit
import AVFoundation
import Metal

/// What the shader needs to draw one cat. Layout matches `CatU` in Shaders.metal.
struct CatU {
    var center: V2 = .zero, half_: V2 = .zero
    var spine: (SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>) = (.zero, .zero, .zero, .zero)
    var head: SIMD4<Float> = .zero
    var headDir: V2 = V2(1, 0), shadowOffset: V2 = V2(5, 9)
    var tail: (SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>) = (.zero, .zero, .zero, .zero, .zero, .zero, .zero, .zero, .zero)
    var paws: (SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>, SIMD4<Float>) = (.zero, .zero, .zero, .zero, .zero, .zero, .zero, .zero)
    var base: SIMD4<Float> = .zero, stripe: SIMD4<Float> = .zero, belly: SIMD4<Float> = .zero
    var whitePaws: Float = 0, pattern: Float = 0, alpha: Float = 1, eyesOpen: Float = 1
    var earL: Float = 0, earR: Float = 0, pad0: Float = 0, pad1: Float = 0
}

/// Coats are designed colourways, not attempts at real fur. Pattern 1 is saddle bands, 2 a tuxedo.
struct CatCoat {
    var base, stripe, belly: SIMD3<Float>
    var whitePaws, pattern: Float
    static let ginger = CatCoat(base: SIMD3(0.70, 0.49, 0.30), stripe: SIMD3(0.50, 0.30, 0.16), belly: SIMD3(0.92, 0.80, 0.62), whitePaws: 1, pattern: 1)
    static let tuxedo = CatCoat(base: SIMD3(0.20, 0.20, 0.22), stripe: SIMD3(0.20, 0.20, 0.22), belly: SIMD3(0.86, 0.85, 0.83), whitePaws: 1, pattern: 2)
    static let charcoal = CatCoat(base: SIMD3(0.17, 0.175, 0.19), stripe: SIMD3(0.17, 0.175, 0.19), belly: SIMD3(0.17, 0.175, 0.19), whitePaws: 0, pattern: 0)
    static let silver = CatCoat(base: SIMD3(0.63, 0.63, 0.62), stripe: SIMD3(0.40, 0.40, 0.40), belly: SIMD3(0.91, 0.90, 0.88), whitePaws: 1, pattern: 1)
}

/// A pose, seen from directly above: hips, spine, head, paws and tail, relative to the cat's own heading.
struct CatPose {
    var pos: V2, heading: Float
    var spine: [(Float, Float, Float)]             // along, side, radius: hips first
    var head: (Float, Float, Float)
    var look: Float = 0                            // head turn, radians
    var paws: [(Float, Float, Float)]
    var tailStart: (Float, Float)
    var tailAngle: Float, tailCurl: Float, tailLength: Float = 120, tailWave: Float = 0
    var lift: Float = 0
    var eyesOpen = true
    var earL: Float = 0, earR: Float = 0
    var size: Float = 1
    var blobs: [(Float, Float, Float)] = []       // shoulders and haunches

    func uniforms(coat: CatCoat, time: Float) -> CatU {
        let f = V2(cos(heading), sin(heading)), s = f.perp
        func P(_ a: Float, _ b: Float) -> V2 { pos + (f * a + s * b) * size }
        func J(_ j: (Float, Float, Float)) -> SIMD4<Float> { let p = P(j.0, j.1); return SIMD4(p.x, p.y, j.2 * size, 0) }
        // Slow breathing: barely visible on its own, but it's what stops it reading as a statue.
        let breath = 1 + 0.018 * sin(time * 1.7 + pos.x * 0.01)
        func B(_ j: (Float, Float, Float)) -> SIMD4<Float> { var v = J(j); v.z *= breath; return v }
        var u = CatU()
        u.spine = (B(spine[0]), B(spine[1]), B(spine[2]), B(spine[3]))
        u.head = J(head)
        u.headDir = f.rotated(look)
        u.shadowOffset = V2(5, 9) + V2(10, 18) * lift
        // The tail is a chain that bends a bit more with each joint.
        var joints: [SIMD4<Float>] = []
        var p = P(tailStart.0, tailStart.1), a = heading + tailAngle
        let step = tailLength * size / 8
        for i in 0..<9 {
            let k = Float(i) / 8
            joints.append(SIMD4(p.x, p.y, (9.5 - 2.6 * k) * size, 0))
            a += tailCurl / 8 + sin(time * 2.6 + Float(i) * 0.7) * tailWave * (0.3 + k)
            p += V2(cos(a), sin(a)) * step
        }
        u.tail = (joints[0], joints[1], joints[2], joints[3], joints[4], joints[5], joints[6], joints[7], joints[8])
        var pw = paws.map { J($0) }
        pw += blobs.map { b -> SIMD4<Float> in var v = B(b); v.w = 1; return v }
        while pw.count < 8 { pw.append(.zero) }
        u.paws = (pw[0], pw[1], pw[2], pw[3], pw[4], pw[5], pw[6], pw[7])
        u.base = SIMD4(coat.base.x, coat.base.y, coat.base.z, 1)
        u.stripe = SIMD4(coat.stripe.x, coat.stripe.y, coat.stripe.z, 1)
        u.belly = SIMD4(coat.belly.x, coat.belly.y, coat.belly.z, 1)
        u.whitePaws = coat.whitePaws; u.pattern = coat.pattern
        u.earL = earL; u.earR = earR
        u.eyesOpen = eyesOpen ? 1 : 0
        // Only as big a quad as this pose needs, shadow included: this shader is expensive per pixel.
        var lo = V2(.infinity, .infinity), hi = V2(-Float.infinity, -Float.infinity)
        func grow(_ v: SIMD4<Float>) {
            guard v.z > 0 else { return }
            lo = V2(min(lo.x, v.x - v.z), min(lo.y, v.y - v.z)); hi = V2(max(hi.x, v.x + v.z), max(hi.y, v.y + v.z))
        }
        joints.forEach(grow); pw.forEach(grow)
        [u.spine.0, u.spine.1, u.spine.2, u.spine.3].forEach(grow)
        grow(SIMD4(u.head.x, u.head.y, u.head.z * 1.7, 0))
        let pad = 34 + 1.7 * u.shadowOffset.len
        u.center = (lo + hi) * 0.5; u.half_ = (hi - lo) * 0.5 + V2(pad, pad)
        return u
    }

    /// Sitting tall: wide haunches, narrow chest, paws together, tail round to one side.
    static func sitting(_ pos: V2, _ heading: Float, look: Float = 0, time: Float = 0) -> CatPose {
        CatPose(pos: pos, heading: heading,
                spine: [(-46, 0, 38), (-20, 0, 40), (10, 0, 31), (30, 0, 21)],
                head: (66, 0, 28), look: look,
                paws: CatPose.tucked,
                tailStart: (-60, 24), tailAngle: 2.15, tailCurl: -2.1, tailLength: 120, tailWave: 0.012 + 0.03 * pow(max(0, sin(time * 0.9)), 6),
                blobs: [(-40, -19, 31), (-40, 19, 31), (14, -14, 24), (14, 14, 24)])
    }
    /// The loaf: a neat oval with everything tucked in.
    static func loaf(_ pos: V2, _ heading: Float, look: Float = 0, time: Float = 0, eyes: Bool = false) -> CatPose {
        var p = CatPose(pos: pos, heading: heading,
                spine: [(-48, 0, 36), (-20, 0, 40), (8, 0, 38), (28, 0, 28)],
                head: (66, 0, 28), look: look, paws: CatPose.tucked,
                tailStart: (-68, 22), tailAngle: 2.3, tailCurl: -2.5, tailLength: 118, tailWave: 0.004,
                blobs: [(-38, -17, 30), (-38, 17, 30), (10, -18, 27), (10, 18, 27)])
        p.eyesOpen = eyes
        return p
    }
    /// One foot's position along the body through a stride. In stance it moves backward at exactly
    /// the speed the body moves forward, so the paw stays planted on the floor. Then it swings ahead.
    static func foot(_ phase: Float, reach: Float) -> Float {
        let stance: Float = 0.62
        let p = phase - floor(phase)
        if p < stance { return reach * (1 - 2 * p / stance) }
        return -reach + 2 * reach * smooth(0, 1, (p - stance) / (1 - stance))
    }
    static let strideReach: Float = 25
    /// Strides per second for a given speed, so that planted feet really don't slide.
    static func stridesPerSecond(speed: Float) -> Float { speed * 0.62 / (2 * strideReach) }

    /// Walking: a four-beat gait seen from above. The shoulders and haunches roll with the legs,
    /// the spine sways a little, and the head stays steadier than the body.
    static func walking(_ pos: V2, _ heading: Float, cycle: Float, look: Float = 0, time: Float = 0) -> CatPose {
        let A = strideReach
        let lf = foot(cycle, reach: A), rh = foot(cycle + 0.25, reach: A), rf = foot(cycle + 0.5, reach: A), lh = foot(cycle + 0.75, reach: A)
        let sway = sin(cycle * 2 * .pi)
        return CatPose(pos: pos, heading: heading,
                spine: [(-62, sway * -3.5, 26), (-24, sway * 2, 26), (14, sway * -1.5, 25), (46, sway * 1.2, 19)],
                head: (72, sway * 0.8, 25), look: look - sway * 0.035,
                paws: [(42 + lf, -29, 8.5), (42 + rf, 29, 8.5), (-56 + lh, -32, 9), (-56 + rh, 32, 9)],
                tailStart: (-84, sway * -2), tailAngle: .pi + sway * 0.1, tailCurl: 0.75 + sway * 0.18, tailLength: 112, tailWave: 0.02,
                blobs: [(-54 + (lh - rh) * 0.14, -8, 21), (-54 + (rh - lh) * 0.14, 8, 21), (22 + (lf - rf) * 0.16, -7, 19), (22 + (rf - lf) * 0.16, 7, 19)])
    }
    /// Low and long, tail flicking, with one paw or the other thrown forward as it swats.
    /// Puffed up, it's bigger and rounder, with its tail straight up, so from above it looks short.
    static func crouching(_ pos: V2, _ heading: Float, look: Float = 0, reach: Float, reach2: Float = 0, puff: Float = 0, wiggle: Float, time: Float = 0) -> CatPose {
        let big = 1 + 0.13 * puff
        return CatPose(pos: pos, heading: heading,
                spine: [(-60, wiggle * 5, 27 * big), (-24, wiggle * 2, 27 * big), (14, 0, 26 * big), (42, 0, 21 * big)],
                head: (68 + max(reach, reach2) * 9, 0, 25), look: look,
                paws: [(34 + reach * 84, 22 - reach * 14, 8.5), (34 + reach2 * 84, -22 + reach2 * 14, 8.5), (-46, -31, 9), (-46, 31, 9)],
                tailStart: (-82, wiggle * 7), tailAngle: .pi + wiggle * 0.25, tailCurl: -0.7 * (1 - puff), tailLength: 112 - 58 * puff, tailWave: 0.09,
                blobs: [(-50, -10, 23 * big), (-50, 10, 23 * big), (20, -9, 20 * big), (20, 9, 20 * big)])
    }
    /// Carried: gone long and limp, with its legs and tail hanging down.
    static func limp(_ pos: V2, _ heading: Float, time: Float = 0) -> CatPose {
        let sw = sin(time * 3)
        return CatPose(pos: pos, heading: heading,
                spine: [(-70, 0, 24), (-30, 0, 25), (14, 0, 25), (50, 0, 19)],
                head: (78, 0, 25), look: sw * 0.12,
                paws: [(46, -23, 7.5), (46, 23, 7.5), (-66, -23, 8), (-66, 23, 8)],
                tailStart: (-88, 0), tailAngle: .pi, tailCurl: sw * 0.25, tailLength: 62, tailWave: 0.01,
                blobs: [(-60, -8, 20), (-60, 8, 20), (24, -8, 19), (24, 8, 19)])
    }
    /// Hidden under the body, so sitting cats can stand up without their feet popping into existence.
    static let tucked: [(Float, Float, Float)] = [(16, -11, 8), (16, 11, 8), (-30, -24, 8), (-30, 24, 8)]

    /// Between two poses of the same cat. Every pose has the same parts, which is what makes this work.
    static func mix(_ a: CatPose, _ b: CatPose, _ k: Float) -> CatPose {
        func m(_ x: Float, _ y: Float) -> Float { x + (y - x) * k }
        func t3(_ x: (Float, Float, Float), _ y: (Float, Float, Float)) -> (Float, Float, Float) { (m(x.0, y.0), m(x.1, y.1), m(x.2, y.2)) }
        var p = a
        p.spine = zip(a.spine, b.spine).map(t3)
        p.head = t3(a.head, b.head)
        p.look = m(a.look, b.look)
        p.paws = zip(a.paws, b.paws).map(t3)
        p.blobs = zip(a.blobs, b.blobs).map(t3)
        p.tailStart = (m(a.tailStart.0, b.tailStart.0), m(a.tailStart.1, b.tailStart.1))
        p.tailAngle = m(a.tailAngle, b.tailAngle); p.tailCurl = m(a.tailCurl, b.tailCurl)
        p.tailLength = m(a.tailLength, b.tailLength); p.tailWave = m(a.tailWave, b.tailWave)
        return p
    }
}

extension Renderer {
    /// The clean's own cat, drawn in the same pass as everything else.
    func drawCat(_ sim: Sim, _ e: MTLRenderCommandEncoder, _ g: inout Globals, above: Bool) {
        guard let c = sim.cat, !c.isGone, c.above == above else { return }
        var u = c.pose(time: sim.t, sim: sim).uniforms(coat: c.coat, time: sim.t)
        u.alpha = sim.robotAlpha
        e.setRenderPipelineState(pCat)
        e.setVertexBytes(&u, length: MemoryLayout<CatU>.stride, index: 0)
        e.setVertexBytes(&g, length: MemoryLayout<Globals>.stride, index: 1)
        e.setFragmentBytes(&u, length: MemoryLayout<CatU>.stride, index: 0)
        e.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
    }

    func drawCat(_ u0: CatU, target: MTLTexture) {
        var u = u0
        var g = Globals(viewport: sizePts, time: 0)
        let cb = queue.makeCommandBuffer()!
        let rp = MTLRenderPassDescriptor()
        rp.colorAttachments[0].texture = target
        rp.colorAttachments[0].loadAction = .load
        rp.colorAttachments[0].storeAction = .store
        let e = cb.makeRenderCommandEncoder(descriptor: rp)!
        e.setRenderPipelineState(pCat)
        e.setVertexBytes(&u, length: MemoryLayout<CatU>.stride, index: 0)
        e.setVertexBytes(&g, length: MemoryLayout<Globals>.stride, index: 1)
        e.setFragmentBytes(&u, length: MemoryLayout<CatU>.stride, index: 0)
        e.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
        e.endEncoding()
        cb.commit(); cb.waitUntilCompleted()
    }
}

/// The style test: a designed, matte cat next to the real robot, with only the calm moods.
enum CatTest {
    static let view = V2(1600, 900)

    static func scene(device: MTLDevice, space: CGColorSpace) throws -> (Renderer, Sim) {
        let r = try Renderer(device: device, sizePts: view)
        r.setWallpaper(ThanksVideo.floor(), space: space)
        Sim.dockXOverride = 800
        let s = Sim(size: view, visibleMin: V2(0, 0), visibleMax: view, entries: [])
        s.dockDrop = 1; s.dockAlpha = 0; s.brushSpeed = 26
        return (r, s)
    }

    static func stills(to dir: String) throws {
        let device = MTLCreateSystemDefaultDevice()!
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let (r, s) = try scene(device: device, space: space)
        s.pos = V2(1040, 470); s.heading = -2.2; s.t = 1
        let W = 3840, H = 2160
        let td = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: W, height: H, mipmapped: false)
        td.usage = [.renderTarget, .shaderRead]; td.storageMode = .shared
        let tex = device.makeTexture(descriptor: td)!
        let cb = r.queue.makeCommandBuffer()!
        r.encode(sim: s, target: tex, cb: cb, dt: 1 / 60)
        cb.commit(); cb.waitUntilCompleted()
        var rider = CatPose.sitting(s.pos - V2(cos(s.heading), sin(s.heading)) * 8, s.heading, look: 0.7)
        rider.lift = 0.25; rider.size = 0.78
        let cats: [(CatPose, CatCoat)] = [
            (.sitting(V2(360, 300), -.pi / 2 + 0.25, look: 0.45, time: 1.2), .ginger),
            (.loaf(V2(400, 680), -0.5, time: 1.2), .ginger),
            (.sitting(V2(1370, 720), -2.7, look: -0.2, time: 1.2), .tuxedo),
            (.walking(V2(830, 770), 0.08, cycle: 0.1, time: 1.2), .ginger),
            (.walking(V2(760, 130), 3.2, cycle: 0.55, time: 1.2), .tuxedo),
            (rider, .tuxedo),
        ]
        for (pose, coat) in cats { r.drawCat(pose.uniforms(coat: coat, time: 1.2), target: tex) }
        try Offline.savePNG(tex, to: dir + "/cat-style-test.png", space: space, crop: nil)
    }

    /// A close-up that only starts as the Swatter squares up to the robot, and stops when it's gone.
    static func fightClip(to path: String) throws {
        let device = MTLCreateSystemDefaultDevice()!
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let size = V2(1600, 900), srcW = 3840, srcH = 2160, W = 1920, H = 1080
        let r = try Renderer(device: device, sizePts: size)
        r.setWallpaper(Wallpaper.fallback(size: CGSize(width: 1600, height: 900)), space: space)
        var o = Offline.Options(); o.catMood = .swatter; o.mode = .tidy
        let synth = Synth()
        let s = Offline.makeSim(o, size: size, r: r, space: space, audio: synth)
        var soundL: [Float] = [], soundR: [Float] = []
        let td = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: srcW, height: srcH, mipmapped: false)
        td.usage = [.renderTarget, .shaderRead]; td.storageMode = .shared
        let tex = device.makeTexture(descriptor: td)!
        let videoPath = path + ".video.mp4"
        try? FileManager.default.removeItem(atPath: videoPath)
        let writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: videoPath), fileType: .mp4)
        let vi = AVAssetWriterInput(mediaType: .video, outputSettings: [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: W, AVVideoHeightKey: H, AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 9_000_000]])
        vi.expectsMediaDataInRealTime = false
        let ad = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vi, sourcePixelBufferAttributes: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferWidthKey as String: W, kCVPixelBufferHeightKey as String: H])
        writer.add(vi); writer.startWriting(); writer.startSession(atSourceTime: .zero)
        var frame = 0, tailFrames = 0
        var cam: V2?
        while s.t < 70, tailFrames < 300 {
            s.step(1 / 120); s.step(1 / 120)
            var L = [Float](repeating: 0, count: 800), R = L
            synth.render(800, &L, &R)
            guard let c = s.cat else { break }
            if c.hasFought && cam != nil { tailFrames += 1 }
            if cam == nil {
                guard case .crouching = c.state, (c.pos - s.pos).len < s.R + 420 else { continue }
                cam = (c.pos + s.pos) * 0.5
            }
            soundL += L; soundR += R
            let cb = r.queue.makeCommandBuffer()!
            r.encode(sim: s, target: tex, cb: cb, dt: 1 / 60)
            cb.commit(); cb.waitUntilCompleted()
            while !vi.isReadyForMoreMediaData { RunLoop.main.run(until: Date().addingTimeInterval(0.002)) }
            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, ad.pixelBufferPool!, &pb)
            guard let buf = pb else { continue }
            let scale = Float(srcW) / size.x
            let ox = Int(clampf(cam!.x * scale - Float(W) / 2, 0, Float(srcW - W))), oy = Int(clampf(cam!.y * scale - Float(H) / 2, 0, Float(srcH - H)))
            CVPixelBufferLockBaseAddress(buf, [])
            tex.getBytes(CVPixelBufferGetBaseAddress(buf)!, bytesPerRow: CVPixelBufferGetBytesPerRow(buf), from: MTLRegionMake2D(ox, oy, W, H), mipmapLevel: 0)
            CVPixelBufferUnlockBaseAddress(buf, [])
            ad.append(buf, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 60))
            frame += 1
        }
        vi.markAsFinished()
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        try ThanksVideo.attachSound(soundL, soundR, video: videoPath, to: path)
        print("fight clip", frame, "frames, status", writer.status.rawValue)
    }

    /// One whole mood on the sample mess, with no sound, plus how long each frame took to draw.
    static func moodClip(_ mood: CatActor.Mood?, seconds: Float, to path: String, pickAt: Float? = nil, onRobot: Bool = false) throws {
        let device = MTLCreateSystemDefaultDevice()!
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let size = V2(1600, 900), W = 1920, H = 1080
        let r = try Renderer(device: device, sizePts: size)
        r.setWallpaper(Wallpaper.fallback(size: CGSize(width: 1600, height: 900)), space: space)
        var o = Offline.Options(); o.catMood = mood; o.mode = .tidy
        let synth = Synth()
        let s = Offline.makeSim(o, size: size, r: r, space: space, audio: synth)
        var soundL: [Float] = [], soundR: [Float] = []
        var pick = pickAt
        let td = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: W, height: H, mipmapped: false)
        td.usage = [.renderTarget, .shaderRead]; td.storageMode = .shared
        let tex = device.makeTexture(descriptor: td)!
        let videoPath = path + ".video.mp4"
        try? FileManager.default.removeItem(atPath: videoPath)
        let writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: videoPath), fileType: .mp4)
        let vi = AVAssetWriterInput(mediaType: .video, outputSettings: [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: W, AVVideoHeightKey: H, AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 9_000_000]])
        vi.expectsMediaDataInRealTime = false
        let ad = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vi, sourcePixelBufferAttributes: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferWidthKey as String: W, kCVPixelBufferHeightKey as String: H])
        writer.add(vi); writer.startWriting(); writer.startSession(atSourceTime: .zero)
        var drawTime = 0.0
        let frames = Int(seconds * 60)
        for frame in 0..<frames {
            s.step(1 / 120); s.step(1 / 120)
            Offline.pickTest(&pick, s, onRobot: onRobot)
            var L = [Float](repeating: 0, count: 800), R = L
            synth.render(800, &L, &R)
            soundL += L; soundR += R
            let t0 = CFAbsoluteTimeGetCurrent()
            let cb = r.queue.makeCommandBuffer()!
            r.encode(sim: s, target: tex, cb: cb, dt: 1 / 60)
            cb.commit(); cb.waitUntilCompleted()
            drawTime += CFAbsoluteTimeGetCurrent() - t0
            while !vi.isReadyForMoreMediaData { RunLoop.main.run(until: Date().addingTimeInterval(0.002)) }
            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, ad.pixelBufferPool!, &pb)
            guard let buf = pb else { continue }
            CVPixelBufferLockBaseAddress(buf, [])
            tex.getBytes(CVPixelBufferGetBaseAddress(buf)!, bytesPerRow: CVPixelBufferGetBytesPerRow(buf), from: MTLRegionMake2D(0, 0, W, H), mipmapLevel: 0)
            CVPixelBufferUnlockBaseAddress(buf, [])
            ad.append(buf, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 60))
        }
        vi.markAsFinished()
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        try ThanksVideo.attachSound(soundL, soundR, video: videoPath, to: path)
        print(String(format: "%@ %.2f ms per frame, status %d", (path as NSString).lastPathComponent, drawTime / Double(frames) * 1000, writer.status.rawValue))
    }

    /// Every cat sound, one after another with gaps, to judge by ear. Prints how loud each one is.
    static func soundSampler(to path: String) throws {
        let synth = Synth()
        var out: [Float] = []
        func play(_ name: String, seconds: Float, _ start: () -> Void, end: (() -> Void)? = nil) {
            start()
            let n = Int(seconds * 48000)
            var L = [Float](repeating: 0, count: n), R = L
            synth.render(n, &L, &R)
            end?()
            var quiet = [Float](repeating: 0, count: 24000), q2 = quiet
            synth.render(24000, &quiet, &q2)
            let peak = L.map(abs).max() ?? 0
            let rms = (L.reduce(0) { $0 + $1 * $1 } / Float(n)).squareRoot()
            print(String(format: "%-22@ peak %.2f  rms %.3f", name as NSString, peak, rms))
            out += L + quiet
        }
        play("hiss short", seconds: 0.5, { synth.hiss(0.32) })
        play("hiss long", seconds: 1, { synth.hiss(0.8) })
        play("growl", seconds: 1.5, { synth.growl(1.3) })
        play("paw hits", seconds: 0.7, { for _ in 0..<1 { synth.swat() } })
        play("meow, bumped", seconds: 0.9, { synth.meow(1, rate: 1, amp: 0.85) })
        play("meow, picked up", seconds: 0.8, { synth.meow(3, rate: 1, amp: 0.85) })
        play("meow, annoyed", seconds: 0.7, { synth.meow(2, rate: 0.9, amp: 0.6) })
        play("meow, hello", seconds: 0.8, { synth.meow(0, rate: 1, amp: 0.5) })
        play("yowl, running off", seconds: 0.6, { synth.meow(3, rate: 1.15, amp: 0.7) })
        // The purr eases in and out, so it gets a while.
        play("purr", seconds: 6, { synth.setPurr(1) }, end: { synth.setPurr(0) })
        // Next to the motor, as it would be in a clean.
        synth.setMotor(level: 1, load: 0)
        var warm = [Float](repeating: 0, count: 48000), w2 = warm
        synth.render(48000, &warm, &w2)
        play("motor alone", seconds: 1, {})
        play("hiss over motor", seconds: 1, { synth.hiss(0.8) })
        play("meow over motor", seconds: 1, { synth.meow(1, rate: 1, amp: 0.85) })
        let url = URL(fileURLWithPath: path)
        try? FileManager.default.removeItem(at: url)
        let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
        let file = try AVAudioFile(forWriting: url, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192000])
        let pcm = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(out.count))!
        pcm.frameLength = AVAudioFrameCount(out.count)
        out.withUnsafeBufferPointer {
            pcm.floatChannelData![0].update(from: $0.baseAddress!, count: out.count)
            pcm.floatChannelData![1].update(from: $0.baseAddress!, count: out.count)
        }
        try file.write(from: pcm)
    }

    /// Ten seconds with everything in view, or a close-up that follows the walker.
    static func clip(to path: String, closeUp: Bool = false) throws {
        let device = MTLCreateSystemDefaultDevice()!
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let (r, s) = try scene(device: device, space: space)
        // The close-up renders at double size and crops to the walking cat.
        let W = 1920, H = 1080
        let srcW = closeUp ? 3840 : W, srcH = closeUp ? 2160 : H
        let td = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: srcW, height: srcH, mipmapped: false)
        td.usage = [.renderTarget, .shaderRead]; td.storageMode = .shared
        let tex = device.makeTexture(descriptor: td)!
        try? FileManager.default.removeItem(atPath: path)
        let writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: path), fileType: .mp4)
        let vi = AVAssetWriterInput(mediaType: .video, outputSettings: [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: W, AVVideoHeightKey: H, AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 10_000_000]])
        vi.expectsMediaDataInRealTime = false
        let ad = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: vi, sourcePixelBufferAttributes: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferWidthKey as String: W, kCVPixelBufferHeightKey as String: H])
        writer.add(vi); writer.startWriting(); writer.startSession(atSourceTime: .zero)

        let supPos = V2(330, 330), supHeading: Float = 0.2
        var supLook: Float = 0.5, supTarget: Float = 0.5
        var riderYaw: Float = 0, riderLook: Float = 0.8
        func blink(_ t: Float, every: Float, at: Float) -> Bool { let c = fmodf(t + at, every); return c > 0.17 }
        for frame in 0..<(60 * 10) {
            let t = Float(frame) / 60, dt: Float = 1 / 60
            // The robot weaves, so its passenger has turns to absorb.
            let px = 1050 + 370 * sin(t * 0.62), py = 430 + 190 * sin(t * 1.24)
            let dx = 370 * 0.62 * cos(t * 0.62), dy = 190 * 1.24 * cos(t * 1.24)
            s.t = t
            s.pos = V2(px, py); s.heading = atan2(dy, dx)
            s.brushAngle = t * 26; s.lidarAngle = t * 16; s.ledPulse = t
            let cb = r.queue.makeCommandBuffer()!
            r.encode(sim: s, target: tex, cb: cb, dt: dt)
            cb.commit(); cb.waitUntilCompleted()

            // The Supervisor doesn't track smoothly. It holds, then turns to catch up, like a real watcher.
            let wanted = clampf(wrapAngle(atan2(s.pos.y - supPos.y, s.pos.x - supPos.x) - supHeading), -1.15, 1.15)
            if abs(wanted - supTarget) > 0.3 { supTarget = wanted }
            supLook += (supTarget - supLook) * (1 - exp(-dt * 5.5))
            var sup = CatPose.sitting(supPos, supHeading, look: supLook, time: t)
            sup.eyesOpen = blink(t, every: 3.3, at: 0.7)
            sup.earR = max(0, 1 - abs(t - 2.6) * 7) * 0.55

            // The Loaf is fast asleep and flicks one ear once.
            var loaf = CatPose.loaf(V2(1440, 740), -2.6, time: t)
            loaf.earL = max(0, 1 - abs(t - 4.9) * 8) * 0.6

            // The Rider leans late into each turn, and looks away from wherever it's going.
            let yawErr = wrapAngle(s.heading - riderYaw)
            if frame == 0 { riderYaw = s.heading } else { riderYaw = wrapAngle(riderYaw + yawErr * (1 - exp(-dt * 2.6))) }
            let lookWanted: Float = 0.8 + sin(t * 0.45) * 0.3
            riderLook += (lookWanted - riderLook) * (1 - exp(-dt * 1.4))
            var rider = CatPose.sitting(s.pos - V2(cos(riderYaw), sin(riderYaw)) * 6, riderYaw, look: riderLook, time: t + 1.3)
            rider.lift = 0.25; rider.size = 0.78
            rider.eyesOpen = blink(t, every: 4.1, at: 2)

            // The walker strolls across at an easy pace, with its feet planted as it goes.
            let walkSpeed: Float = 118
            let walkX = 130 + t * walkSpeed
            let walkCycle = t * CatPose.stridesPerSecond(speed: walkSpeed)
            let walker = CatPose.walking(V2(walkX, 775), 0, cycle: walkCycle, look: sin(t * 0.7) * 0.12, time: t)
            for (pose, coat) in [(sup, CatCoat.ginger), (loaf, .ginger), (walker, .ginger), (rider, .tuxedo)] { r.drawCat(pose.uniforms(coat: coat, time: t), target: tex) }

            while !vi.isReadyForMoreMediaData { RunLoop.main.run(until: Date().addingTimeInterval(0.002)) }
            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, ad.pixelBufferPool!, &pb)
            guard let buf = pb else { continue }
            CVPixelBufferLockBaseAddress(buf, [])
            var ox = 0, oy = 0
            if closeUp {
                // Frames the cat, then eases along, so you can see its feet staying put on the floor.
                let scale = Float(srcW) / view.x
                ox = Int(clampf(walkX * scale - Float(W) / 2 + 60, 0, Float(srcW - W)))
                oy = Int(clampf(775 * scale - Float(H) / 2, 0, Float(srcH - H)))
            }
            tex.getBytes(CVPixelBufferGetBaseAddress(buf)!, bytesPerRow: CVPixelBufferGetBytesPerRow(buf), from: MTLRegionMake2D(ox, oy, W, H), mipmapLevel: 0)
            CVPixelBufferUnlockBaseAddress(buf, [])
            ad.append(buf, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 60))
        }
        vi.markAsFinished()
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
        print("clip", writer.status.rawValue, writer.error?.localizedDescription ?? "ok")
    }
}
