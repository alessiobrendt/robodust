import AppKit

/// The cat in a clean. It's only for show: it watches the robot and gets in its way, and the most it
/// can do is make the robot steer around it. Nothing here touches which files get collected.
final class CatActor {
    enum Mood: Int, CaseIterable { case supervisor, loaf, rider, swatter, saboteur, coward }
    enum Next { case sit, loaf, crouch, mount, scheme, hide, peek, vanish }
    enum Rest { case sit, loaf, crouch }
    enum State {
        case waiting
        case walking(to: V2, speed: Float, then: Next)
        case sitting, loafing, crouching
        case climbing(from: V2, fromHeading: Float)
        case riding
        case hopping(from: V2, to: V2)
        case swatting
        case scheming
        case startled, cowering, peeking
        case gone
    }

    static var trace = false
    static let floorSize: Float = 0.85, riderSize: Float = 0.8

    let mood: Mood
    let coat: CatCoat
    var state = State.waiting
    var stateT: Float = 0
    var pos = V2(-1000, -1000), heading: Float = 0
    var rest = Rest.sit
    var walkK: Float = 0, cycle: Float = 0
    var look: Float = 0, lookTarget: Float = 0
    var size = CatActor.floorSize, hop: Float = 0
    var reach: Float = 0, reach2: Float = 0, puff: Float = 0, fightSide: Float = 1, wiggle: Float = 0, earsBack: Float = 0, earFlick: Float = 0
    var nextFlick: Float = 4
    var eyeOpenUntil: Float = 0
    var bumps = 0, noteUntil: Float = 0
    var spoke = false, growled = false, hissedStare = 0, disturbed = false, hasFought = false

    // The Saboteur.
    var target: Item?
    var pushDir = V2(0, 0), pushes = 0, pushT: Float = -1, planRow = 0
    static let maxPushes = 3
    var yawLag: Float = 0

    // Being carried.
    var picked = false, pickK: Float = 0
    var home: (spot: V2, then: Next)?
    enum AfterHop { case watch, home, loaf, fight }
    var afterHop = AfterHop.watch
    var seatOffset = V2(0, 0)

    /// Every cat sounds a little different, even though it's always the same recording.
    let voice = Float.random(in: 0.94...1.08)

    func say(_ sim: Sim, _ which: Int, rate: Float = 1, amp: Float) { sim.audio?.meow(which, rate: rate * voice, amp: amp) }

    init(mood: Mood, coat: CatCoat) { self.mood = mood; self.coat = coat }

    /// One cat per clean, with a random mood and coat. Dark coats disappear on the robot's black top,
    /// so a Rider is always a light one.
    static func random() -> CatActor { pick(mood: Mood.allCases.randomElement()!) }
    static func pick(mood: Mood) -> CatActor {
        let coats: [CatCoat] = mood == .rider ? [.ginger, .silver] : [.ginger, .tuxedo, .charcoal, .silver]
        return CatActor(mood: mood, coat: coats.randomElement()!)
    }

    /// On board it purrs. A Loaf on the floor purrs quietly, until it's been walked into or picked up.
    var purrLevel: Float {
        if picked { return 0 }
        switch state {
        case .riding: return 1
        case .loafing: return bumps == 0 && !disturbed ? 0.3 : 0
        default: return 0
        }
    }

    /// A cat on board is real weight: the motor labours under it, and stops the moment it's off.
    var loadOnRobot: Float {
        if picked { return 0 }
        if case .riding = state { return 0.32 }
        return 0
    }

    func set(_ s: State, _ sim: Sim) {
        state = s; stateT = 0
        if CatActor.trace { print(String(format: "%6.1fs  cat: %@", sim.t, "\(s)")) }
    }

    var isGone: Bool { if case .gone = state { return true }; if case .waiting = state { return true }; return false }
    /// Riding, climbing and hopping are drawn over the robot. So is a cat being carried.
    var above: Bool {
        if picked || pickK > 0.05 { return true }
        switch state { case .climbing, .riding, .hopping: return true; default: return false }
    }

    // MARK: - Update

    func update(_ sim: Sim, _ dt: Float) {
        stateT += dt
        pickK += ((picked ? 1 : 0) - pickK) * min(1, dt * 11)
        earFlick *= exp(-dt * 7)
        if sim.t > nextFlick {
            earFlick = 0.6
            nextFlick = sim.t + Float.random(in: 3...8)
        }
        if picked { easeWalk(0, dt); return }

        let over = [.done, .tidying, .tidied, .puttingBack, .leaving, .finished].contains(sim.phase)
        if over {
            switch state {
            case .waiting: set(.gone, sim)
            // Out of sight, it just stays there.
            case .cowering: set(.gone, sim)
            case .gone, .hopping: break
            case .walking(_, _, .vanish): break
            case .riding, .climbing: hopOff(sim)
            default: leave(sim, speed: 190)
            }
        }

        switch state {
        case .waiting: waitToEnter(sim)
        case .gone: break
        case .walking(let to, let speed, let then): walk(sim, to, speed, then, dt)
        case .sitting: sit(sim, dt)
        case .loafing: loaf(sim, dt)
        case .crouching: crouch(sim, dt)
        case .climbing(let from, let fromHeading): climb(sim, from, fromHeading, dt)
        case .riding: ride(sim, dt)
        case .hopping(let from, let to): hopTo(sim, from, to, dt)
        case .swatting: swat(sim, dt)
        case .scheming: scheme(sim, dt)
        case .startled: startle(sim, dt)
        case .cowering: cower(sim, dt)
        case .peeking: peek(sim, dt)
        }
    }

    func waitToEnter(_ sim: Sim) {
        switch mood {
        case .supervisor, .coward:
            guard sim.phase == .scanning else { return }
            enter(sim, at: openSpot(sim), speed: 210, then: .sit)
        case .rider:
            guard sim.phase == .undocking else { return }
            enter(sim, at: sim.pos, speed: 230, then: .mount)
        case .loaf, .swatter:
            guard sim.phase == .cleaning else { return }
            let then: Next = mood == .loaf ? .loaf : .crouch
            enter(sim, at: laneSpot(sim) ?? openSpot(sim), speed: 250, then: then)
        case .saboteur:
            guard sim.phase == .cleaning else { return }
            if let plan = sabotagePlan(sim) {
                target = plan.item; pushDir = plan.dir; planRow = plan.row
                enter(sim, at: plan.spot, speed: 320, then: .scheme)
            } else { enter(sim, at: openSpot(sim), speed: 210, then: .sit) }
        }
    }

    func enter(_ sim: Sim, at target: V2, speed: Float, then: Next) {
        let off = 150 * size + 60
        let edges: [(Float, V2)] = [
            (target.x, V2(-off, target.y)), (sim.size.x - target.x, V2(sim.size.x + off, target.y)),
            (target.y, V2(target.x, -off)), (sim.size.y - target.y, V2(target.x, sim.size.y + off)),
        ]
        var start = edges.min { $0.0 < $1.0 }!.1
        // Not straight out of the dock's tower.
        if sim.insideKeepOut(V2(start.x, min(start.y, sim.wall.max.y))) { start.x = sim.dockPos.x + sim.R * 5 * (sim.dockPos.x < sim.size.x / 2 ? 1 : -1) }
        pos = start
        heading = atan2(target.y - pos.y, target.x - pos.x)
        walkK = 1
        set(.walking(to: target, speed: speed, then: then), sim)
    }

    func walk(_ sim: Sim, _ target0: V2, _ speed: Float, _ then: Next, _ dt: Float) {
        var target = target0
        switch then {
        case .mount:
            // The robot has moved on before the cat got there, so it just watches instead.
            guard sim.phase == .undocking || sim.phase == .scanning else { rest = .sit; set(.sitting, sim); return }
            let away = (pos - sim.pos).unit
            target = sim.pos + away * (sim.R + 76)
        case .scheme:
            // The file went before the cat got there, or the robot is about to: on to the next one.
            if let t = self.target, !t.onFloor || t.icon.ingesting || t.grabbed || (t.icon.pos - sim.pos).len < sim.R + 160 { plotNext(sim); return }
        case .loaf, .crouch:
            // The robot got there first, so pick a spot further along its route.
            if (target - sim.pos).len < sim.R + 150, sim.phase == .cleaning, let next = laneSpot(sim, after: sim.pathI + 1) {
                set(.walking(to: next, speed: speed, then: then), sim); return
            }
        default: break
        }
        let d = target - pos
        // Waits for the robot to stop before it tries to get on.
        if then == .mount && d.len < 8 && sim.phase != .scanning { easeWalk(0, dt); return }
        if d.len < (then == .vanish ? 14 : 8) {
            arrive(then, sim)
            return
        }
        stepToward(target, speed: speed, turn: then == .vanish || speed > 400 ? 8 : 3.6, dt)
        lookTarget = 0
        look += (lookTarget - look) * (1 - exp(-dt * 6))
        keepClear(sim)
    }

    func arrive(_ then: Next, _ sim: Sim) {
        switch then {
        case .sit: rest = .sit; set(.sitting, sim)
        case .loaf: rest = .loaf; set(.loafing, sim)
        case .crouch: rest = .crouch; set(.crouching, sim)
        case .mount: set(.climbing(from: pos, fromHeading: heading), sim)
        case .scheme: rest = .crouch; set(.scheming, sim)
        case .hide: rest = .sit; set(.cowering, sim)
        case .peek: rest = .crouch; set(.peeking, sim)
        case .vanish: set(.gone, sim)
        }
    }

    func stepToward(_ target: V2, speed: Float, turn: Float, _ dt: Float) {
        let d = target - pos
        let err = wrapAngle(atan2(d.y, d.x) - heading)
        heading = wrapAngle(heading + clampf(err * 5, -turn, turn) * dt)
        let align = clampf(1 - abs(err) / 1.7, 0.1, 1)
        easeWalk(1, dt)
        let sp = speed * align * walkK
        pos += V2(cos(heading), sin(heading)) * (sp * dt)
        // Feet stay planted: the stride rate comes from the real speed. Turning on the spot still steps.
        cycle += max(CatPose.stridesPerSecond(speed: sp / size), abs(err) > 0.3 ? 0.9 : 0) * dt
    }

    func easeWalk(_ to: Float, _ dt: Float) { walkK += (to - walkK) * (1 - exp(-dt * 8)) }

    /// A cat on the floor never ends up under the robot.
    func keepClear(_ sim: Sim) {
        let d = pos - sim.pos, l = d.len, clear = sim.R + 62
        if l < clear { pos = sim.pos + d.unit * clear }
        // Around the dock, not over it.
        if sim.dockAlpha > 0.5, sim.insideKeepOut(pos), pos.y < sim.wall.max.y {
            let o = pos - sim.dockPos
            let u = o.dot(sim.dockOut), side = o.dot(sim.dockOut.perp)
            let toFront = sim.R * 2 - u, toSide = sim.R * 2.3 - abs(side)
            if toFront < toSide { pos += sim.dockOut * toFront } else { pos += sim.dockOut.perp * (side < 0 ? -toSide : toSide) }
        }
    }

    func leave(_ sim: Sim, speed: Float) {
        let off = 150 * size + 80
        let options: [(Float, V2)] = [
            (pos.x, V2(-off, pos.y)), (sim.size.x - pos.x, V2(sim.size.x + off, pos.y)),
            (pos.y, V2(pos.x, -off)), (sim.size.y - pos.y, V2(pos.x, sim.size.y + off)),
        ]
        set(.walking(to: options.min { $0.0 < $1.0 }!.1, speed: speed, then: .vanish), sim)
    }

    /// Bolts away from the robot, then sits at a safe distance to keep an eye on it.
    func flee(_ sim: Sim) {
        hasFought = true
        var spot = clampSpot(pos + (pos - sim.pos).unit * 520, sim)
        if sim.insideKeepOut(spot) || (spot - sim.pos).len < sim.R + 260 { spot = openSpot(sim) }
        set(.walking(to: spot, speed: 600, then: .sit), sim)
    }

    /// Holds still, then turns to catch up, like a real watcher.
    func watch(_ sim: Sim, _ dt: Float, limit: Float = 1.15) {
        let wanted = wrapAngle(atan2(sim.pos.y - pos.y, sim.pos.x - pos.x) - heading)
        let held = clampf(wanted, -limit, limit)
        if abs(held - lookTarget) > 0.3 { lookTarget = held }
        look += (lookTarget - look) * (1 - exp(-dt * 5.5))
        // Too far round to follow with its head: shuffles the whole body.
        if abs(wanted) > limit + 0.35 {
            heading = wrapAngle(heading + (wanted > 0 ? 1 : -1) * 1.1 * dt)
            cycle += 0.9 * dt
            easeWalk(0.3, dt)
        } else { easeWalk(0, dt) }
    }

    func sit(_ sim: Sim, _ dt: Float) {
        watch(sim, dt)
        earsBack *= exp(-dt * 0.5)
        if !spoke, stateT > 1.6 {
            spoke = true
            say(sim, 0, amp: 0.5)
        }
        keepClear(sim)
        // The Coward doesn't wait to see what the robot does, and the dock emptying is far too loud.
        if mood == .coward, (pos - sim.pos).len < sim.R + 520 || (sim.phase == .emptying && sim.phaseT > 0.7) { startleNow(sim); return }
        // The robot's coming this way: gets up and moves, unhurried, but not slowly.
        let toCat = pos - sim.pos
        if toCat.len < sim.R + 215, sim.v > 40, toCat.unit.dot(sim.fwd) > 0.25 {
            var spot = pos + sim.fwd.perp * (toCat.dot(sim.fwd.perp) >= 0 ? 340 : -340)
            spot = clampSpot(spot, sim)
            if (spot - sim.pos).len < sim.R + 200 || sim.insideKeepOut(spot) { spot = clampSpot(pos - sim.fwd.perp * (toCat.dot(sim.fwd.perp) >= 0 ? 340 : -340), sim) }
            // Nowhere better to go, up against a wall: it stays put, and the robot steers around it.
            if (spot - pos).len > 80 { set(.walking(to: spot, speed: 300, then: .sit), sim) }
        }
    }

    func loaf(_ sim: Sim, _ dt: Float) {
        easeWalk(0, dt)
        keepClear(sim)
        look += (0 - look) * (1 - exp(-dt * 4))
        // Won't move for a robot. But once it's only chasing the last few files, the cat has had enough.
        if sim.phase == .hunting { leave(sim, speed: 170) }
    }

    func crouch(_ sim: Sim, _ dt: Float) {
        let toRobot = sim.pos - pos, l = toRobot.len
        if sim.phase != .cleaning && sim.phase != .hunting && sim.phase != .returning { leave(sim, speed: 220); return }
        // Always facing its target, ears going flat as it gets close.
        let err = wrapAngle(atan2(toRobot.y, toRobot.x) - heading)
        heading = wrapAngle(heading + clampf(err * 6, -4.5, 4.5) * dt)
        look += (clampf(err, -0.5, 0.5) - look) * (1 - exp(-dt * 8))
        let close = 1 - smooth(sim.R + 170, sim.R + 400, l)
        earsBack += (close - earsBack) * (1 - exp(-dt * 7))
        wiggle += ((close > 0.3 ? 1 : 0) - wiggle) * (1 - exp(-dt * 6))
        if !growled, close > 0.25 { growled = true; sim.audio?.growl(1.3) }
        // Waited long enough: creeps closer, low to the ground.
        if stateT > 3, l > sim.R + 100, abs(err) < 0.8 {
            let sp: Float = 130
            pos += V2(cos(heading), sin(heading)) * (sp * dt)
            cycle += CatPose.stridesPerSecond(speed: sp / size) * dt
            easeWalk(0.3, dt)
        } else { easeWalk(0, dt); if abs(err) > 0.5 { cycle += 0.9 * dt } }
        // Gave up waiting altogether.
        if stateT > 26 { leave(sim, speed: 300) }
    }

    /// The fight. Side-on, it hits the robot in flurries, backs off with its tail up to stare it
    /// down, and goes back in. The robot just stops and takes it, until it twitches and the cat runs.
    func swat(_ sim: Sim, _ dt: Float) {
        let t = stateT
        let toCat = (pos - sim.pos).unit
        let near = sim.R + 84, far = sim.R + 142
        let each: Float = 0.14
        let first: (from: Float, hits: Float) = (0, 4), second: (from: Float, hits: Float) = (1.5, 5)
        let backAt1 = first.hits * each, staredAt1 = backAt1 + 0.85
        let backAt2 = second.from + second.hits * each, end = backAt2 + 0.85
        var dist = near, stare: Float = 0
        reach = 0; reach2 = 0
        func flurry(_ from: Float, _ hits: Float) {
            let x = (t - from) / each
            guard x >= 0, x < hits else { return }
            let i = floor(x), k = pow(sin((x - i) * .pi), 2)
            if Int(i) % 2 == 0 { reach = k } else { reach2 = k }
            if x - i >= 0.5, x - i - dt / each < 0.5 { landHit(sim, toCat) }
        }
        flurry(first.from, first.hits)
        flurry(second.from, second.hits)
        if t >= backAt1 && t < second.from {
            dist = near + (far - near) * (smooth(0, 0.22, t - backAt1) - smooth(0, 0.14, t - staredAt1))
            stare = smooth(0, 0.2, t - backAt1) * (1 - smooth(0, 0.14, t - staredAt1))
        } else if t >= backAt2 {
            dist = near + (far - near) * smooth(0, 0.22, t - backAt2)
            stare = smooth(0, 0.2, t - backAt2)
        }
        // Both times it backs off, it hisses.
        if hissedStare == 0, t >= backAt1 { hissedStare = 1; sim.audio?.hiss(0.8) }
        if hissedStare == 1, t >= backAt2 { hissedStare = 2; sim.audio?.hiss(0.75) }
        hop = 0.22 * (bump(t - backAt1) + bump(t - backAt2) + bump(t - staredAt1))
        // The robot twitches, and the cat has had enough.
        if t >= end - 0.2, t - dt < end - 0.2 { sim.audio?.chirp([(392, 0.06), (494, 0.08)]); sim.shake = max(sim.shake, 0.5) }
        pos = sim.pos + toCat * dist
        let side: Float = 0.2 + 0.4 * stare
        let toRobot = atan2(-toCat.y, -toCat.x)
        let want = wrapAngle(toRobot + (fightSide > 0 ? side : -side))
        heading = wrapAngle(heading + clampf(wrapAngle(want - heading) * 9, -7, 7) * dt)
        // Body turned away, but its head never leaves the robot.
        look = wrapAngle(toRobot - heading)
        puff += (stare - puff) * (1 - exp(-dt * 12))
        earsBack = 1
        wiggle = 0
        easeWalk(0, dt)
        if t > end {
            hop = 0; puff = 0; reach = 0; reach2 = 0
            sim.maneuver = (0.5, 0.5, fightSide > 0 ? 1 : -1)
            say(sim, 3, rate: 1.15, amp: 0.7)
            flee(sim)
        }
    }

    func bump(_ x: Float) -> Float { x > 0 && x < 0.22 ? sin(x / 0.22 * .pi) : 0 }

    func landHit(_ sim: Sim, _ toCat: V2) {
        sim.bumper = 1
        sim.shake = max(sim.shake, 0.55)
        sim.audio?.swat()
        sim.audio?.chirp([(740 + Float.random(in: 0...180), 0.04)])
        // Knocks the robot about a bit, and loses some fur doing it.
        sim.pos -= toCat * 2.6
        sim.heading = wrapAngle(sim.heading + Float.random(in: -0.035...0.035))
        let at = sim.pos + toCat * (sim.R + 6)
        let c = coat.base
        for _ in 0..<5 {
            let v = (toCat.rotated(Float.random(in: -1.1...1.1))) * Float.random(in: 60...210)
            sim.spawn(at, v, life: Float.random(in: 0.35...0.8), size: Float.random(in: 2...4.2), color: SIMD4(c.x, c.y, c.z, 0.9), chip: 0)
        }
    }

    func climb(_ sim: Sim, _ from: V2, _ fromHeading: Float, _ dt: Float) {
        let T: Float = 1.5
        // Looks down first, tests it with a paw, then carefully gets on.
        let k = clampf(stateT / T, 0, 1)
        let move = smooth(0.28, 1, k)
        let seat = sim.pos - sim.fwd * 8
        pos = from + (seat - from) * move
        let toSeat = wrapAngle(atan2(seat.y - from.y, seat.x - from.x) - fromHeading)
        heading = wrapAngle(fromHeading + toSeat * smooth(0, 0.3, k))
        look += ((k < 0.3 ? 0.25 : 0) - look) * (1 - exp(-dt * 6))
        rest = .sit
        walkK = 1 - smooth(0.62, 1, k)
        if k > 0.28 && k < 0.95 { cycle += 1.1 * dt }
        size = CatActor.floorSize + (CatActor.riderSize - CatActor.floorSize) * move
        hop = 0.3 * move
        if k >= 1 {
            yawLag = heading
            lookTarget = 0.8
            sim.audio?.thud(0.08)
            say(sim, 0, amp: 0.45)
            set(.riding, sim)
        }
    }

    func ride(_ sim: Sim, _ dt: Float) {
        // Heading home: it's getting off before the dock.
        if sim.phase == .returning || sim.phase == .docking { hopOff(sim); return }
        easeWalk(0, dt)
        seatOffset = seatOffset * exp(-dt * 7)
        size += (CatActor.riderSize - size) * (1 - exp(-dt * 7))
        hop += (0.3 - hop) * (1 - exp(-dt * 7))
        pos = sim.pos - sim.fwd * 8 + seatOffset
        // Leans late into each turn, looking away from wherever it's going.
        let err = wrapAngle(sim.heading - yawLag)
        yawLag = wrapAngle(yawLag + err * (1 - exp(-dt * 2.2)))
        heading = yawLag
        if rest == .loaf { lookTarget = 0 }
        else if stateT.truncatingRemainder(dividingBy: 5) < dt { lookTarget = Float.random(in: 0.5...1) * (Bool.random() ? 1 : -1) }
        look += (lookTarget - look) * (1 - exp(-dt * 2))
    }

    func hopOff(_ sim: Sim) {
        let s = sim.fwd.perp
        var to = sim.pos + s * (sim.R + 95)
        let other = sim.pos - s * (sim.R + 95)
        if sim.insideKeepOut(to) || to != clampSpot(to, sim) { to = other }
        set(.hopping(from: pos, to: clampSpot(to, sim)), sim)
    }

    func hopTo(_ sim: Sim, _ from: V2, _ to: V2, _ dt: Float) {
        let T: Float = 0.42, k = clampf(stateT / T, 0, 1)
        pos = from + (to - from) * smooth(0, 1, k)
        hop = 0.3 * (1 - k) + sin(k * .pi) * 0.55
        size = CatActor.riderSize + (CatActor.floorSize - CatActor.riderSize) * k
        let want = atan2(to.y - from.y, to.x - from.x)
        heading = wrapAngle(heading + clampf(wrapAngle(want - heading) * 8, -6, 6) * dt)
        easeWalk(0.6, dt)
        if k >= 1 {
            hop = 0
            sim.audio?.thud(0.14)
            rest = .sit
            keepClear(sim)
            let over = [.done, .tidying, .tidied, .puttingBack, .leaving, .finished].contains(sim.phase)
            if over { leave(sim, speed: 190); return }
            switch afterHop {
            case .watch: set(.walking(to: openSpot(sim), speed: 190, then: .sit), sim)
            case .loaf: set(.walking(to: openSpot(sim), speed: 190, then: .loaf), sim)
            case .home:
                if let h = home { set(.walking(to: h.spot, speed: 200, then: h.then), sim) }
                else { arrive(mood == .loaf ? .loaf : .sit, sim) }
            case .fight:
                rest = .crouch
                fightSide = Bool.random() ? 1 : -1
                noteUntil = sim.t + 4.5
                hissedStare = 0
                set(.swatting, sim)
            }
        }
    }

    // MARK: - The Saboteur

    /// A file on the robot's route, far enough ahead for the cat to get there first. The cat crouches
    /// on the route side of it, so a swipe sends it sliding out of the robot's way.
    func sabotagePlan(_ sim: Sim) -> (item: Item, spot: V2, dir: V2, row: Int)? {
        guard pushes < CatActor.maxPushes, sim.path.count > 1 else { return nil }
        let start = max(sim.pathI, 0)
        guard start + 1 < sim.path.count else { return nil }
        // How far the robot drives before it starts each stretch of its route.
        var routeTo = (sim.path[start] - sim.pos).len
        for i in start..<(sim.path.count - 1) {
            let a = sim.path[i], b = sim.path[i + 1], seg = b - a, l = seg.len
            defer { routeTo += l }
            guard l > sim.R * 2 else { continue }
            let along = seg / l, across = along.perp
            // Rows only: the robot turns at the ends, so a file there might never be passed.
            guard abs(along.y) < 0.2 else { continue }
            let candidates = sim.items.filter { it in
                guard it.onFloor, !it.icon.ingesting, !it.grabbed, it !== target else { return false }
                let d = it.icon.pos - a, t = d.dot(along)
                guard t > l * 0.15 && t < l * 0.85 && abs(d.dot(across)) < sim.R * 0.9 else { return false }
                // The cat has to get there, and settle, well before the robot does.
                let robotTime = (routeTo + t) / 330, catTime = distanceToCat(it.icon.pos, sim) / 320
                return robotTime > catTime + 1
            }.sorted { ($0.icon.pos - a).dot(along) < ($1.icon.pos - a).dot(along) }
            for it in candidates {
                let side = (it.icon.pos - a).dot(across)
                for sign: Float in [side >= 0 ? 1 : -1, side >= 0 ? -1 : 1] {
                    let dir = across * sign
                    let spot = it.icon.pos - dir * 80 * size, landing = it.icon.pos + dir * 240
                    // Somewhere the file can actually slide to, with room for the cat beside it.
                    if spot != clampToScreen(spot, sim) || sim.insideKeepOut(spot) { continue }
                    if landing != clampToScreen(landing, sim) || sim.insideKeepOut(landing) { continue }
                    return (it, spot, dir, i)
                }
            }
        }
        return nil
    }

    /// Before it's arrived, the cat is as far away as the nearest edge of the screen.
    func distanceToCat(_ p: V2, _ sim: Sim) -> Float {
        if case .waiting = state { return min(p.x, sim.size.x - p.x, p.y, sim.size.y - p.y) + 150 * size }
        return (p - pos).len
    }

    func plotNext(_ sim: Sim) {
        pushT = -1; reach = 0
        if sim.phase == .cleaning, let plan = sabotagePlan(sim) {
            target = plan.item; pushDir = plan.dir; planRow = plan.row
            set(.walking(to: plan.spot, speed: 320, then: .scheme), sim)
        } else {
            // Done, or nothing left to knock over: it sits back to admire its work.
            target = nil
            set(.walking(to: openSpot(sim), speed: 220, then: .sit), sim)
        }
    }

    /// Crouched by a file, eyes on the robot. When it gets close, one swipe sends the file sliding.
    func scheme(_ sim: Sim, _ dt: Float) {
        guard let t = target, t.onFloor, !t.icon.ingesting, !t.grabbed, sim.phase == .cleaning else { plotNext(sim); return }
        let toFile = t.icon.pos - pos
        let want = atan2(toFile.y, toFile.x)
        heading = wrapAngle(heading + clampf(wrapAngle(want - heading) * 6, -4, 4) * dt)
        let toRobot = wrapAngle(atan2(sim.pos.y - pos.y, sim.pos.x - pos.x) - heading)
        easeWalk(0, dt)
        if pushT < 0 {
            // Watching the robot come, ready to swipe.
            look += (clampf(toRobot, -0.9, 0.9) - look) * (1 - exp(-dt * 6))
            earsBack += (0.5 - earsBack) * (1 - exp(-dt * 4))
            wiggle += (0.35 - wiggle) * (1 - exp(-dt * 4))
            // Swipes once the robot is driving down this row towards the file.
            let r = t.icon.pos - sim.pos
            let onRow = sim.pathI == planRow + 1
            if stateT > 0.3, onRow, r.len < sim.R + 290, r.unit.dot(sim.fwd) > 0.2 { pushT = 0 }
            // The robot has gone past this row, or it's taking far too long: try another file.
            if sim.pathI > planRow + 1 || stateT > 40 { plotNext(sim) }
            return
        }
        // The swipe, then a look straight at the robot, as if nothing happened.
        let before = pushT
        pushT += dt
        let k = clampf(pushT / 0.3, 0, 1)
        reach = pow(sin(k * .pi), 2)
        wiggle = 0
        look += (0 - look) * (1 - exp(-dt * 10))
        if before < 0.15, pushT >= 0.15 {
            t.icon.vel += pushDir * 1350 + pushDir.perp * Float.random(in: -140...140)
            t.icon.angV += Float.random(in: -6...6)
            t.icon.agit = max(t.icon.agit, 0.35)
            pushes += 1
            noteUntil = sim.t + 3
            if CatActor.trace { print(String(format: "%6.1fs  cat: pushed a file away (%d)", sim.t, pushes)) }
            sim.audio?.tick(); sim.audio?.thud(0.06)
        }
        if pushT > 0.3 { look += (clampf(toRobot, -1.1, 1.1) - look) * (1 - exp(-dt * 8)); earsBack *= exp(-dt * 3) }
        if pushT > 0.75 {
            if pushes == 1 { say(sim, 0, rate: 1.1, amp: 0.4) }
            plotNext(sim)
        }
    }

    // MARK: - The Coward

    /// Somewhere to hide: under a window, or just off the edge of the screen. The cat sits at `inside`,
    /// out of sight, and peeks from `peek`, with only its head showing.
    struct Hideout { var cover: CGRect; var inside: V2; var peek: V2; var outward: V2 }
    var hideout: Hideout?
    var checkT: Float = 0

    /// Where the windows are, in screen points from the top left. Only their outlines are read,
    /// never what's in them. Test renders have no windows.
    static var windows: () -> [CGRect] = {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        let me = ProcessInfo.processInfo.processIdentifier
        return list.compactMap { w in
            guard (w[kCGWindowLayer as String] as? Int) == 0, (w[kCGWindowOwnerPID as String] as? Int32) != me,
                  ((w[kCGWindowAlpha as String] as? Double) ?? 1) > 0.5,
                  let b = w[kCGWindowBounds as String] as? NSDictionary, let r = CGRect(dictionaryRepresentation: b as CFDictionary),
                  r.width >= 260, r.height >= 180 else { return nil }
            return r
        }
    }

    func findHideout(_ sim: Sim) -> Hideout {
        let screen = CGRect(x: 0, y: 0, width: CGFloat(sim.size.x), height: CGFloat(sim.size.y))
        var options: [(Hideout, Float)] = []
        func score(_ h: Hideout) -> Float { (h.peek - pos).len - 0.4 * (h.peek - sim.pos).len }
        // Under a window, from the nearest edge of it that's on screen.
        for w in CatActor.windows() {
            let r = w.intersection(screen)
            guard r.width >= 220, r.height >= 160 else { continue }
            let c = V2(clampf(pos.x, Float(r.minX), Float(r.maxX)), clampf(pos.y, Float(r.minY), Float(r.maxY)))
            let edges: [(Float, V2)] = [(abs(c.x - Float(r.minX)), V2(-1, 0)), (abs(Float(r.maxX) - c.x), V2(1, 0)),
                                        (abs(c.y - Float(r.minY)), V2(0, -1)), (abs(Float(r.maxY) - c.y), V2(0, 1))]
            let out = edges.min { $0.0 < $1.0 }!.1
            var edge = c
            if out.x < 0 { edge.x = Float(r.minX) } else if out.x > 0 { edge.x = Float(r.maxX) }
            if out.y < 0 { edge.y = Float(r.minY) } else if out.y > 0 { edge.y = Float(r.maxY) }
            if out.x != 0 { edge.y = clampf(edge.y, Float(r.minY) + 90, Float(r.maxY) - 90) } else { edge.x = clampf(edge.x, Float(r.minX) + 90, Float(r.maxX) - 90) }
            let h = Hideout(cover: r, inside: edge - out * 150 * size, peek: edge - out * 42 * size, outward: out)
            // A peek spot out in the open, not off the screen or in the dock.
            let look = edge + out * 40
            if look != clampToScreen(look, sim) || sim.insideKeepOut(look) { continue }
            options.append((h, score(h)))
        }
        // Off the edge of the screen. The menu bar and the Dock count as edges too.
        let bands: [(CGRect, V2)] = [
            (CGRect(x: -400, y: -400, width: 400, height: CGFloat(sim.size.y) + 800), V2(1, 0)),
            (CGRect(x: CGFloat(sim.size.x), y: -400, width: 400, height: CGFloat(sim.size.y) + 800), V2(-1, 0)),
            (CGRect(x: -400, y: -400, width: CGFloat(sim.size.x) + 800, height: 400 + CGFloat(sim.wall.min.y)), V2(0, 1)),
            (CGRect(x: -400, y: CGFloat(sim.wall.max.y), width: CGFloat(sim.size.x) + 800, height: 400), V2(0, -1)),
        ]
        for (band, inward) in bands {
            var edge = pos
            if inward.x > 0 { edge.x = 0 } else if inward.x < 0 { edge.x = sim.size.x }
            if inward.y > 0 { edge.y = sim.wall.min.y } else if inward.y < 0 { edge.y = sim.wall.max.y }
            edge = V2(clampf(edge.x, 120, sim.size.x - 120), clampf(edge.y, sim.wall.min.y + 120, sim.wall.max.y - 120))
            if inward.x > 0 { edge.x = 0 } else if inward.x < 0 { edge.x = sim.size.x }
            if inward.y > 0 { edge.y = sim.wall.min.y } else if inward.y < 0 { edge.y = sim.wall.max.y }
            if sim.insideKeepOut(edge + inward * 60) { continue }
            let out = inward
            let h = Hideout(cover: band, inside: edge - out * 170 * size, peek: edge - out * 42 * size, outward: out)
            options.append((h, score(h) + 120))
        }
        return options.min { $0.1 < $1.1 }!.0
    }

    /// Puffed up, hissing, then gone.
    func startleNow(_ sim: Sim) {
        rest = .crouch
        noteUntil = sim.t + 3
        sim.audio?.hiss(0.45)
        set(.startled, sim)
    }

    func startle(_ sim: Sim, _ dt: Float) {
        let away = pos - sim.pos
        let want = atan2(away.y, away.x)
        heading = wrapAngle(heading + clampf(wrapAngle(want + .pi - heading) * 8, -7, 7) * dt)
        look = 0
        puff += (1 - puff) * (1 - exp(-dt * 14))
        earsBack = 1
        hop = 0.18 * (stateT < 0.2 ? sin(stateT / 0.2 * .pi) : 0)
        easeWalk(0, dt)
        keepClear(sim)
        if stateT > 0.6 {
            hop = 0
            let h = findHideout(sim)
            hideout = h
            set(.walking(to: h.inside, speed: 560, then: .hide), sim)
        }
    }

    /// Out of sight. Every so often, once the robot is well away, it creeps out for a look.
    func cower(_ sim: Sim, _ dt: Float) {
        guard let h = hideout else { startleNow(sim); return }
        puff *= exp(-dt * 1.5)
        earsBack *= exp(-dt * 0.4)
        turnTo(atan2(h.outward.y, h.outward.x), dt)
        easeWalk(0, dt)
        if !stillHidden(sim) { return }
        if stateT > 4, (h.peek - sim.pos).len > 700, sim.phase == .cleaning || sim.phase == .hunting || sim.phase == .returning {
            set(.walking(to: h.peek, speed: 90, then: .peek), sim)
        }
    }

    /// Just its head out, watching the robot. Back under the moment it comes this way.
    func peek(_ sim: Sim, _ dt: Float) {
        guard let h = hideout else { startleNow(sim); return }
        puff *= exp(-dt * 2)
        turnTo(atan2(h.outward.y, h.outward.x), dt)
        let toRobot = wrapAngle(atan2(sim.pos.y - pos.y, sim.pos.x - pos.x) - heading)
        look += (clampf(toRobot, -1, 1) - look) * (1 - exp(-dt * 5))
        earsBack += (0.6 - earsBack) * (1 - exp(-dt * 3))
        easeWalk(0, dt)
        if !stillHidden(sim) { return }
        let r = pos - sim.pos
        if r.len < sim.R + 480 || stateT > 9 {
            set(.walking(to: h.inside, speed: r.len < sim.R + 480 ? 480 : 140, then: .hide), sim)
        }
    }

    func turnTo(_ want: Float, _ dt: Float) { heading = wrapAngle(heading + clampf(wrapAngle(want - heading) * 5, -4, 4) * dt) }

    /// Windows move and close. If its hiding place has gone, it finds another, fast.
    func stillHidden(_ sim: Sim) -> Bool {
        checkT += 1 / 120
        guard checkT > 1.5, let h = hideout else { return true }
        checkT = 0
        let onScreen = h.cover.minX >= 0 && h.cover.maxX <= CGFloat(sim.size.x) && h.cover.minY >= 0 && h.cover.maxY <= CGFloat(sim.size.y)
        guard onScreen else { return true }
        let p = CGPoint(x: CGFloat(h.inside.x), y: CGFloat(h.inside.y))
        if CatActor.windows().contains(where: { $0.contains(p) }) { return true }
        startleNow(sim)
        return false
    }

    // MARK: - Choosing spots

    func clampSpot(_ p: V2, _ sim: Sim) -> V2 {
        let m: Float = 170
        return V2(clampf(p.x, sim.wall.min.x + m, sim.wall.max.x - m), clampf(p.y, sim.wall.min.y + m, sim.wall.max.y - m))
    }

    /// Somewhere with a view, clear of the robot and its dock.
    func openSpot(_ sim: Sim) -> V2 {
        var best = clampSpot(sim.pos + V2(0, -420), sim), bestScore: Float = -1
        for _ in 0..<12 {
            let p = clampSpot(V2(Float.random(in: 0...sim.size.x), Float.random(in: 0...sim.size.y)), sim)
            if sim.insideKeepOut(p) { continue }
            let d = (p - sim.pos).len
            // Near enough to supervise, but not in the way.
            let score = d < sim.R + 300 ? 0 : 1000 - abs(d - 620)
            if score > bestScore { bestScore = score; best = p }
        }
        return best
    }

    /// A point on the robot's route, ahead of it, that the cat has time to walk to.
    func laneSpot(_ sim: Sim, after start: Int? = nil) -> V2? {
        guard sim.path.count > 1 else { return nil }
        let from = start ?? max(sim.pathI, Int(Float(sim.path.count) * 0.3))
        guard from + 1 < sim.path.count else { return nil }
        for i in from..<(sim.path.count - 1) {
            let a = sim.path[i], b = sim.path[i + 1]
            guard (b - a).len > sim.R * 2.4 else { continue }
            let p = a + (b - a) * Float.random(in: 0.35...0.65)
            if p != clampSpot(p, sim) || sim.insideKeepOut(p) || (p - sim.pos).len < sim.R + 260 { continue }
            return p
        }
        return nil
    }

    // MARK: - Meeting the robot

    /// Asked each tick while the robot follows its path. True means the robot should bump and back off.
    func robotContact(_ sim: Sim) -> Bool {
        guard !picked else { return false }
        let d = pos - sim.pos
        switch state {
        case .loafing:
            guard bumps < 2, d.len < sim.R + 74, d.dot(sim.fwd) > 0 else { return false }
            bumps += 1
            if CatActor.trace { print(String(format: "%6.1fs  cat: bumped %d", sim.t, bumps)) }
            noteUntil = sim.t + 3
            eyeOpenUntil = sim.t + 1.6
            earFlick = 1
            if bumps == 1 { say(sim, 2, rate: 0.9, amp: 0.6) }
            else { say(sim, 1, amp: 0.85) }
            return true
        case .crouching:
            guard d.len < sim.R + 106, d.dot(sim.fwd) > 0 else { return false }
            fightSide = Bool.random() ? 1 : -1
            noteUntil = sim.t + 4.5
            sim.audio?.hiss(0.32)
            set(.swatting, sim)
            return false
        default: return false
        }
    }

    /// Who the robot is steering around, once it knows better than to bump them.
    var avoid: Float? {
        guard !picked else { return nil }
        switch state {
        case .loafing: return bumps >= 2 ? 92 + 80 : nil
        case .sitting, .scheming: return 92 + 72
        default: return nil
        }
    }

    /// Bends the robot's aim around the cat. It only changes the route, never what gets collected.
    func steer(_ aim: V2, _ sim: Sim) -> V2 {
        guard let r = avoid, sim.phase == .cleaning || sim.phase == .returning else { return aim }
        let d = sim.pos - pos, l = d.len
        guard l < r + 120 else { return aim }
        let toAim = (aim - sim.pos).unit, out = d.unit
        guard toAim.dot(out) < 0.2 else { return aim }
        var tangent = out.perp
        if tangent.dot(toAim) < 0 { tangent = tangent * -1 }
        let w = 1 - clampf((l - r) / 120, 0, 1)
        let dir = toAim * (1 - w) + tangent * w + out * (max(0, r - l) / 30)
        return sim.pos + dir.unit * 240
    }

    /// A lane stop under a cat the robot has given up on is skipped. Whatever it missed gets picked up later.
    func covers(_ target: V2, _ sim: Sim) -> Bool {
        guard sim.phase == .cleaning, let r = avoid else { return false }
        return (target - pos).len < r
    }

    /// While the Rider is getting on, the robot waits for it.
    func holdsRobot(_ sim: Sim) -> Bool {
        guard sim.phaseT < 10 else { return false }
        switch state {
        case .climbing: return true
        case .walking(_, _, .mount): return true
        default: return false
        }
    }

    func note(_ sim: Sim) -> String? {
        guard sim.t < noteUntil else { return nil }
        switch mood {
        case .swatter: return "Under attack"
        case .saboteur: return "The cat knocked a file away"
        case .coward: return "The cat ran off to hide"
        default: return "There's a cat in the way"
        }
    }

    /// The robot stops dead while it's being hit.
    var fighting: Bool { if case .swatting = state { return true }; return false }

    // MARK: - Being picked up

    func hit(_ p: V2, _ sim: Sim) -> Bool {
        switch state {
        // A cat on board comes first: anywhere on the robot picks up the cat, not the robot.
        case .riding: return (p - sim.pos).len < sim.R
        case .sitting, .loafing, .crouching, .scheming, .peeking: break
        case .walking(_, _, let then): if then == .vanish || then == .mount { return false }
        default: return false
        }
        return (p - pos).len < 88 * size
    }

    func pick(_ sim: Sim) {
        guard !picked else { return }
        switch state {
        case .sitting: home = (pos, .sit)
        case .loafing: home = (pos, .loaf)
        case .crouching: home = (pos, .crouch)
        case .scheming: home = (pos, .scheme); pushT = -1; reach = 0
        case .peeking: home = hideout.map { ($0.inside, .hide) }
        case .walking(let to, _, let then): home = (to, then)
        case .riding:
            // Lifted off the robot, it's a floor cat again, and it stays wherever it's put.
            home = nil
            hop = 0; size = CatActor.floorSize; seatOffset = V2(0, 0)
            set(rest == .loaf ? .loafing : .sitting, sim)
        default: home = nil
        }
        picked = true
        disturbed = true
        lookTarget = 0
        say(sim, 3, amp: 0.85)
    }

    func drag(to p: V2, _ sim: Sim) {
        let d = p - pos
        // Swings a little as it's carried, then settles.
        heading = wrapAngle(heading + clampf(d.x * 0.0009, -0.03, 0.03))
        pos = V2(clampf(p.x, sim.wall.min.x + 60, sim.wall.max.x - 60), clampf(p.y, sim.wall.min.y + 60, sim.wall.max.y - 60))
    }

    /// Put down, it walks straight back to where it was. Put down on the robot, it depends on the cat.
    func drop(_ sim: Sim) {
        guard picked else { return }
        picked = false
        sim.audio?.thud(0.1)
        let d = pos - sim.pos
        if d.len < sim.R {
            let canRide = [.undocking, .scanning, .cleaning, .hunting].contains(sim.phase) && sim.robotAlpha > 0.5
            if canRide {
                switch mood {
                case .loaf: board(sim, resting: .loaf); return
                case .rider: say(sim, 0, amp: 0.45); board(sim, resting: .sit); return
                case .supervisor where Bool.random(): board(sim, resting: .sit); return
                case .swatter:
                    sim.audio?.hiss(0.5)
                    afterHop = .fight
                    hopDown(sim)
                    return
                default: break
                }
            }
            say(sim, 2, rate: 0.92, amp: 0.5)
            afterHop = .home
            hopDown(sim)
            return
        }
        say(sim, 2, rate: 0.92, amp: 0.5)
        // Dropped half on the robot's edge: hops clear, rather than being shoved.
        if d.len < sim.R + 62 {
            afterHop = .home
            hopDown(sim)
            return
        }
        if let h = home, (h.spot - pos).len > 20 { set(.walking(to: h.spot, speed: 200, then: h.then), sim) }
    }

    /// Stays on for the ride, sitting or as a loaf. It still gets off before the dock.
    func board(_ sim: Sim, resting: Rest) {
        rest = resting
        afterHop = resting == .loaf ? .loaf : .watch
        seatOffset = pos - (sim.pos - sim.fwd * 8)
        yawLag = heading
        lookTarget = resting == .loaf ? 0 : 0.8
        set(.riding, sim)
    }

    func hopDown(_ sim: Sim) {
        let d = pos - sim.pos
        let dir = d.len > 1 ? d.unit : sim.fwd * -1
        var to = sim.pos + dir * (sim.R + 78)
        if sim.insideKeepOut(to) || to != clampToScreen(to, sim) { to = sim.pos - dir * (sim.R + 78) }
        set(.hopping(from: pos, to: clampToScreen(to, sim)), sim)
    }

    func clampToScreen(_ p: V2, _ sim: Sim) -> V2 {
        let m: Float = 60
        return V2(clampf(p.x, sim.wall.min.x + m, sim.wall.max.x - m), clampf(p.y, sim.wall.min.y + m, sim.wall.max.y - m))
    }

    // MARK: - Drawing

    func pose(time t: Float, sim: Sim) -> CatPose {
        let r: CatPose
        switch rest {
        case .sit: r = .sitting(pos, heading, look: look, time: t)
        case .loaf: r = .loaf(pos, heading, look: look, time: t)
        case .crouch: r = .crouching(pos, heading, look: look, reach: reach, reach2: reach2, puff: puff, wiggle: wiggle * sin(t * 21), time: t)
        }
        let w = CatPose.walking(pos, heading, cycle: cycle, look: look, time: t)
        var p = CatPose.mix(r, w, smooth(0, 1, walkK))
        if pickK > 0.001 { p = CatPose.mix(p, .limp(pos, heading, time: t), smooth(0, 1, pickK)) }
        var riding = false
        switch state { case .riding, .climbing, .hopping: riding = true; default: break }
        let liftNow = riding ? sim.lift * 0.9 : 0
        p.lift = max(hop, pickK) + liftNow
        p.size = size * (1 + 0.12 * pickK + (riding ? 0.13 * sim.lift : 0))
        if case .riding = state { p.pos = sim.pos - sim.fwd * 8 + seatOffset }
        // Eyes: loaves sleep, unless a robot has just walked into them. Everyone else blinks.
        let blinking = fmodf(t + 0.7, 3.3) < 0.15
        p.eyesOpen = rest == .loaf && walkK < 0.5 && pickK < 0.5 ? t < eyeOpenUntil : !blinking
        p.earL = earFlick * (Int(nextFlick) % 2 == 0 ? 1 : 0) - earsBack
        p.earR = earFlick * (Int(nextFlick) % 2 == 0 ? 0 : 1) - earsBack
        return p
    }
}
