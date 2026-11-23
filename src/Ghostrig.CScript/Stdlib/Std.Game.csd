/// Game helpers (Document 7 §13.13): the simulation side of mini-games — fixed-step loops, easing, colours, collision,
/// spatial hashing, rigid bodies, state machines, grid pathfinding, cameras, tile maps, particles and input maps.
/// Drawing (Sprite, SpriteSheet, GameLoop.Attach to Gfx.OnFrame) arrives with the Gfx library in M4.
module Std.Game;

import Std.Math;
import Std.Collections;
import Std.Random;

/// Fixed-timestep accumulator: feed it frame time, run Update as many times as it says.
class GameLoop {
    GameLoop(Duration step);
    Duration Step { get; }
    /// Adds elapsed frame time and returns how many fixed updates are due (capped at maxSteps; excess time is dropped).
    int Advance(Duration elapsed, int maxSteps);
    /// Fraction of a step left over, for interpolating rendering (0 ≤ alpha < 1).
    float Alpha { get; }
    int TotalSteps { get; }
}

/// Easing curves on t in [0, 1].
class Easing {
    static float Linear(float t);
    static float InQuad(float t);
    static float OutQuad(float t);
    static float InOutQuad(float t);
    static float InCubic(float t);
    static float OutCubic(float t);
    static float InOutCubic(float t);
    static float InSine(float t);
    static float OutSine(float t);
    static float InOutSine(float t);
    static float OutBack(float t);
    static float OutElastic(float t);
    static float OutBounce(float t);
    /// By name: "linear", "inQuad", "outCubic", "outBounce"…
    static float Apply(string name, float t);
}

/// A value animated from one number to another over a duration.
class Tween {
    Tween(float from, float to, Duration duration, string easing);
    float ValueAt(Duration elapsed);
    bool IsDone(Duration elapsed);
}

class Color : Equatable<Color>, Hashable {
    Color(int r, int g, int b);
    Color(int r, int g, int b, int a);
    /// "#rgb", "#rrggbb" or "#rrggbbaa".
    static Color Parse(string hex);
    /// Hue in degrees, saturation and value in [0, 1].
    static Color FromHsv(float hue, float saturation, float value);
    static Color Black { get; }
    static Color White { get; }
    static Color Transparent { get; }
    int R { get; }
    int G { get; }
    int B { get; }
    int A { get; }
    Color WithAlpha(int alpha);
    Color Lerp(Color other, float t);
    Color Darken(float amount);
    Color Lighten(float amount);
    /// Relative luminance (WCAG) in [0, 1].
    float Luminance { get; }
    string ToHex();
    bool Equals(Color other);
    int HashCode();
    string ToString();
}

/// Named palettes for the rig's displays.
class Palette {
    /// Green phosphor terminal shades, dark to bright.
    static Color[] Phosphor { get; }
    static Color[] Amber { get; }
    /// The 16-colour CGA set.
    static Color[] Cga { get; }
    /// Colour-blind-safe categorical palette (Okabe–Ito).
    static Color[] Accessible { get; }
    /// The palette entry closest to a colour.
    static Color Nearest(Color[] palette, Color color);
}

/// Collision tests that report penetration for resolving overlaps.
class Collision {
    static bool RectRect(Rect a, Rect b);
    static bool CircleCircle(Circle a, Circle b);
    static bool CircleRect(Circle circle, Rect rect);
    static bool PolygonPolygon(Polygon a, Polygon b);
    static bool PointInRect(Vector2 point, Rect rect);
    /// The smallest translation that moves a out of b, or null when they do not overlap.
    static Vector2? RectPenetration(Rect a, Rect b);
    static Vector2? CirclePenetration(Circle a, Circle b);
    /// Time of impact in [0, 1] of a rect moving by velocity against a static rect (swept AABB), or null.
    static float? Sweep(Rect moving, Vector2 velocity, Rect target);
}

/// Buckets items by cell for fast neighbourhood queries.
class SpatialHash<T> {
    SpatialHash(float cellSize);
    int Count { get; }
    void Insert(T item, Rect bounds);
    bool Remove(T item);
    void Update(T item, Rect bounds);
    /// Items whose bounds overlap the area, in insertion order.
    T[] Query(Rect area);
    T[] QueryPoint(Vector2 point);
    void Clear();
}

/// Semi-implicit Euler rigid body (no rotation).
class Body {
    Body(Vector2 position, float mass);
    Vector2 Position { get; set; }
    Vector2 Velocity { get; set; }
    float Mass { get; }
    /// Linear damping per second in [0, 1).
    float Damping { get; set; }
    void ApplyForce(Vector2 force);
    void ApplyImpulse(Vector2 impulse);
    /// Integrates accumulated forces over dt seconds and clears them.
    void Step(float dt);
}

/// A finite state machine over string states and events.
class StateMachine {
    StateMachine(string initial);
    string State { get; }
    StateMachine Allow(string from, string trigger, string to);
    bool Can(string trigger);
    /// Raises Error for a trigger the current state does not allow.
    void Fire(string trigger);
    /// Like Fire, but returns false instead of raising.
    bool TryFire(string trigger);
    /// Called with (from, trigger, to) after each transition.
    void OnTransition(fn(string, string, string) -> void handler);
    string[] History { get; }
}

/// Grid pathfinding (A* with Manhattan or octile distance).
class Pathfinding {
    /// Cells where walkable is true; returns the path including both ends, or null.
    static (int, int)[]? FindPath(Grid2D<bool> walkable, (int, int) start, (int, int) goal, bool diagonal);
    /// Breadth-first flood distances from start (-1 = unreachable).
    static Grid2D<int> DistanceField(Grid2D<bool> walkable, (int, int) start);
}

/// Maps between world and screen coordinates.
class Camera2D {
    Camera2D(float viewportWidth, float viewportHeight);
    Vector2 Position { get; set; }
    float Zoom { get; set; }
    float Rotation { get; set; }
    Vector2 WorldToScreen(Vector2 world);
    Vector2 ScreenToWorld(Vector2 screen);
    Rect VisibleArea { get; }
    /// Moves Position toward a target by a fraction (0 = stay, 1 = snap).
    void Follow(Vector2 target, float smoothing);
}

/// A grid of tile ids with a tile size in world units.
class TileMap {
    TileMap(int width, int height, float tileSize);
    int Width { get; }
    int Height { get; }
    float TileSize { get; }
    int Get(int x, int y);
    void Set(int x, int y, int tile);
    void Fill(int tile);
    (int, int) WorldToTile(Vector2 world);
    Rect TileBounds(int x, int y);
    /// Tiles overlapping an area, as (x, y) pairs.
    (int, int)[] TilesIn(Rect area);
    /// Rows of tile ids parsed from text, one character per tile ('#' = 1, '.' = 0, digits as themselves).
    static TileMap Parse(string text, float tileSize);
}

class Particle {
    Vector2 Position { get; }
    Vector2 Velocity { get; }
    float Age { get; }
    float Lifetime { get; }
    Color Color { get; }
}

/// A deterministic particle simulation (seeded).
class ParticleSystem {
    ParticleSystem(int seed, int maxParticles);
    int Count { get; }
    Vector2 Gravity { get; set; }
    /// Emits count particles from origin with random direction within spread (radians) around angle and speed in [minSpeed, maxSpeed].
    void Emit(Vector2 origin, int count, float angle, float spread, float minSpeed, float maxSpeed, float lifetime, Color color);
    void Step(float dt);
    Particle[] Particles { get; }
    void Clear();
}

/// Binds input names (keys, buttons) to game actions.
class InputMap {
    InputMap();
    InputMap Bind(string action, string input);
    void Unbind(string action, string input);
    /// Actions bound to an input.
    string[] ActionsFor(string input);
    string[] InputsFor(string action);
    /// Records an input going down or up (fed from Input events in M4).
    void Press(string input);
    void Release(string input);
    bool IsActive(string action);
    /// True only on the first Update after the action became active.
    bool WasTriggered(string action);
    /// Ends a frame: clears the triggered flags.
    void Update();
}
