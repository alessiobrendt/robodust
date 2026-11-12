using System.Collections.Generic;

namespace RoboDust;

/// What the cat needs from the sim that isn't public anyway.
public sealed partial class Sim
{
    /// The clean's own cat, if there is one. Only for show: it never decides what gets collected.
    public CatActor Cat;

    /// The robot's current route. The cat reads it to find a spot ahead of the robot.
    public IReadOnlyList<V2> CatPath => path;

    /// The cat has had enough: the robot backs off and swings round, the same as after a bump.
    public void CatManeuver(float reverse, float turn, float dir) => maneuver = (reverse, turn, dir);
}
