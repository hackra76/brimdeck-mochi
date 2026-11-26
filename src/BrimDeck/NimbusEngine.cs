using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public enum NimbusEyeShape
{
    Pill, Wide, Dot, Line, Flat, Happy, Closed,
    Spiral, Heart, Star, Tired, Wink, Cup
}

public enum NimbusBadgeKind
{
    Dots, Bang, Question, Dot, Arrow
}

public readonly record struct NimbusBadge(NimbusBadgeKind Kind, Color Color);

internal enum NimbusPropKey
{
    Yaw, Pitch, Roll, Tilt, Open, Sx, Sy,
    Oy, Ox, Tint, Hands, Blush, Es, BadgeS
}

internal enum NimbusEase { Linear, Out, InOut, Back }

internal readonly record struct NimbusTweenKey(double Target, double DurationMs, NimbusEase Ease);

internal sealed class NimbusTween
{
    public NimbusPropKey Prop { get; init; }
    public NimbusTweenKey[] Keys { get; init; } = [];
    public int Index { get; set; }
    public double From { get; set; }
    public double StartMs { get; set; }
    public Action? OnComplete { get; init; }
}

internal sealed class NimbusParticle
{
    public required string Type { get; init; } // "heart" | "star" | "spark" | "sweat" | "z"
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Age { get; set; }
    public double Life { get; set; }
    public double Rot { get; set; }
    public double Size { get; set; }
}

internal sealed record NimbusStateCfg(
    Color Color,
    double Tint,
    NimbusEyeShape Eye,
    NimbusBadge? Badge,
    bool Bounces = false,
    bool Scans = false,
    bool Breathes = false,
    bool Zz = false,
    bool Sweat = false,
    (double Yaw, double Pitch)? Look = null,
    double Tilt = 0);

/// <summary>
/// Procedural vector animation engine for Nimbus, ported from Coucou's BotEngine
/// to native WPF DrawingContext rendering.
/// </summary>
public sealed class NimbusEngine
{
    private const double EyeW = 0.24;
    private const double EyeH = 0.24;
    private const double EyeSp = 0.34;
    private const double EyeP = -0.02;

    private static readonly Color BaseTop = Color.FromRgb(0xFB, 0xFD, 0xFF);
    private static readonly Color BaseBottom = Color.FromRgb(0xD6, 0xE6, 0xF8);
    private static readonly Color InkColor = Color.FromRgb(22, 32, 54);
    private static readonly Color MiniInkColor = Color.FromRgb(18, 28, 48);

    private static readonly Dictionary<NimbusState, NimbusStateCfg> StateConfigs = new()
    {
        [NimbusState.Idle] = new(Color.FromRgb(186, 224, 255), 0.06, NimbusEyeShape.Pill, null),
        [NimbusState.Working] = new(Color.FromRgb(56, 189, 248), 0.68, NimbusEyeShape.Pill, new(NimbusBadgeKind.Dots, Color.FromRgb(56, 189, 248))),
        [NimbusState.Thinking] = new(Color.FromRgb(129, 140, 248), 0.68, NimbusEyeShape.Pill, new(NimbusBadgeKind.Dots, Color.FromRgb(129, 140, 248)), Look: (0.55, 0.55)),
        [NimbusState.Searching] = new(Color.FromRgb(45, 212, 191), 0.68, NimbusEyeShape.Pill, new(NimbusBadgeKind.Dots, Color.FromRgb(45, 212, 191)), Scans: true),
        [NimbusState.Approval] = new(Color.FromRgb(251, 191, 36), 0.74, NimbusEyeShape.Wide, new(NimbusBadgeKind.Bang, Color.FromRgb(251, 191, 36)), Bounces: true),
        [NimbusState.Question] = new(Color.FromRgb(34, 211, 238), 0.70, NimbusEyeShape.Pill, new(NimbusBadgeKind.Question, Color.FromRgb(34, 211, 238)), Tilt: 0.17),
        [NimbusState.Error] = new(Color.FromRgb(248, 113, 113), 0.76, NimbusEyeShape.Flat, new(NimbusBadgeKind.Dot, Color.FromRgb(248, 113, 113))),
        [NimbusState.Finished] = new(Color.FromRgb(52, 211, 153), 0.38, NimbusEyeShape.Happy, new(NimbusBadgeKind.Dot, Color.FromRgb(52, 211, 153))),
        [NimbusState.RateLimit] = new(Color.FromRgb(251, 146, 60), 0.70, NimbusEyeShape.Tired, new(NimbusBadgeKind.Dot, Color.FromRgb(251, 146, 60)), Sweat: true),
        [NimbusState.Sleeping] = new(Color.FromRgb(148, 163, 198), 0.34, NimbusEyeShape.Closed, null, Breathes: true, Zz: true),
        [NimbusState.Dizzy] = new(Color.FromRgb(232, 121, 249), 0.68, NimbusEyeShape.Spiral, null),
    };

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double NowSec() => Clock.Elapsed.TotalSeconds;
    private static double NowMs() => Clock.Elapsed.TotalMilliseconds;

    private readonly Random _rng = new();
    private readonly Dictionary<NimbusPropKey, NimbusTween> _tweens = new();
    private readonly HashSet<NimbusPropKey> _locks = new();
    private readonly List<NimbusParticle> _particles = new();
    private readonly List<double> _slapTimes = new();

    public bool IsMini { get; set; }
    public NimbusState State { get; private set; } = NimbusState.Idle;
    public NimbusQuotaMood QuotaMood { get; set; } = NimbusQuotaMood.Normal;
    public event Action? DizzyTriggered;

    // Animated properties
    private double _yaw, _pitch, _roll, _tilt, _open = 1;
    private double _sx = 1, _sy = 1, _oy, _ox;
    private double _tint, _hands, _blush, _es = 1, _badgeS;

    // Soft-part spring physics (lags behind head turns, hops, and rolls)
    private double _physDx, _physDy, _physVx, _physVy;
    private double _prevYaw, _prevOy, _prevRoll;

    // Mouth slot spring (gulp / chew)
    private double _slotH, _slotHTarget, _slotHVel;
    private bool _isChewing;

    // Targets
    private double _tgYaw, _tgPitch, _tgTilt, _tgSx = 1, _tgSy = 1;
    private double _lookX, _lookY;
    private Color _col = StateConfigs[NimbusState.Idle].Color;
    private Color _colT = StateConfigs[NimbusState.Idle].Color;
    private NimbusStateCfg _cfg = StateConfigs[NimbusState.Idle];

    private NimbusEyeShape? _eyeOverride;
    private double _eyeOverrideUntil;
    private NimbusEyeShape? _permanentEye;
    private NimbusEmote? _permanentEmote;
    private double _miniNextBehavior;
    private double _miniLookX, _miniLookY, _miniLookNextTime;
    private NimbusBadge? _badge;
    private string _badgeKey = "none";
    private int _badgeToken;

    private double _lastTime = NowSec();
    private double _t0 = NowSec();
    private double _nextBlink = NowSec() + 1.5;
    private double _nextAmbient = NowSec() + 2.5;
    private double _nextParticle = NowSec() + 1.0;
    private double _externalLookUntil;
    private double _waveStart, _waveUntil;
    private int _greetToken;
    private bool _isDancing;
    private double _dancingLevel;
    private NimbusOutfit _outfit = NimbusOutfit.Auto;
    private bool _mailbox;
    private double _mailboxLevel;
    private Color? _updateHintColor;
    private double _launchGreetingStart;
    private double _launchGreetingUntil;

    /// <summary>True when Nimbus is resting (Idle/Sleeping) with no active tweens, particles, or dance.</summary>
    public bool IsLowRateIdle =>
        _tweens.Count == 0
        && _particles.Count == 0
        && !_isDancing
        && _dancingLevel <= 0.001
        && _eyeOverride is null
        && (State == NimbusState.Idle || State == NimbusState.Sleeping);

    public void SetDancing(bool dancing) => _isDancing = dancing;
    public void SetOutfit(NimbusOutfit outfit) => _outfit = outfit;
    public void SetMailbox(bool mailbox) => _mailbox = mailbox;
    public void SetUpdateHint(Color? color) => _updateHintColor = color;

    public void SetPermanentEmote(NimbusEmote? emote)
    {
        _permanentEmote = emote;
        double n = NowSec();
        if (emote == NimbusEmote.Wink)
        {
            _miniNextBehavior = n + 0.8 + _rng.NextDouble() * 1.7;
            return;
        }
        _permanentEye = emote switch
        {
            NimbusEmote.Love => NimbusEyeShape.Heart,
            NimbusEmote.Surprised => NimbusEyeShape.Dot,
            NimbusEmote.Proud => NimbusEyeShape.Star,
            NimbusEmote.Yawn => NimbusEyeShape.Tired,
            NimbusEmote.Happy => NimbusEyeShape.Happy,
            NimbusEmote.Annoyed => NimbusEyeShape.Line,
            _ => null
        };
        if (_permanentEye is not null)
        {
            _eyeOverride = _permanentEye;
            _eyeOverrideUntil = double.PositiveInfinity;
        }
        else if (double.IsPositiveInfinity(_eyeOverrideUntil))
        {
            _eyeOverride = null;
            _eyeOverrideUntil = 0;
        }
        _miniNextBehavior = n + 0.8 + _rng.NextDouble() * 1.7;
    }

    public void StartLaunchGreeting()
    {
        Greet();
        double t = NowSec();
        _launchGreetingStart = t;
        _launchGreetingUntil = t + 2.6;
        _oy = -0.65;
        _ox = -1.5;
        Anim(NimbusPropKey.Oy, [new(0.10, 240, NimbusEase.Out), new(-0.24, 180, NimbusEase.Out), new(0, 220, NimbusEase.Back)]);
        Anim(NimbusPropKey.Sy, [new(1.0, 200, NimbusEase.Linear), new(0.68, 90, NimbusEase.Out), new(1.16, 140, NimbusEase.Out), new(1, 220, NimbusEase.InOut)]);
        Anim(NimbusPropKey.Sx, [new(1.0, 200, NimbusEase.Linear), new(1.28, 90, NimbusEase.Out), new(0.90, 140, NimbusEase.Out), new(1, 220, NimbusEase.InOut)]);
        Anim(NimbusPropKey.Ox, [new(0.65, 420, NimbusEase.Back), new(0.65, 850, NimbusEase.Linear), new(0, 380, NimbusEase.InOut)]);
        Anim(NimbusPropKey.Blush, [new(0.85, 250, NimbusEase.Out), new(0.85, 1100, NimbusEase.Linear), new(0, 450, NimbusEase.InOut)]);
        Emit("spark", 6);
    }

    public void Poke()
    {
        Squash();
        NimbusSound.Play(NimbusSoundKind.Poke);
        TriggerEmote(NimbusEmote.Happy, 1.1);
    }

    public void SetState(NimbusState next, bool force = false)
    {
        if (State == next && !force) return;
        var prev = State;
        State = next;
        _cfg = StateConfigs[next];
        _colT = _cfg.Color;
        _tint = _cfg.Tint;
        _tgTilt = _cfg.Tilt;
        SetBadge(_cfg.Badge);

        switch (next)
        {
            case NimbusState.Finished:
                DoRoll(950, 1);
                Emit("spark", 5);
                NimbusSound.Play(NimbusSoundKind.Finished);
                break;
            case NimbusState.Error:
                Anim(NimbusPropKey.Ox, [new(0.08, 50, NimbusEase.Out), new(-0.08, 70, NimbusEase.InOut), new(0.05, 70, NimbusEase.InOut), new(0, 90, NimbusEase.Out)]);
                NimbusSound.Play(NimbusSoundKind.Error);
                break;
            case NimbusState.Approval:
                Anim(NimbusPropKey.Oy, [new(-0.2, 150, NimbusEase.Out), new(0, 300, NimbusEase.Back)]);
                NimbusSound.Play(NimbusSoundKind.Approval);
                break;
            case NimbusState.Dizzy:
                DoRoll(1300, 2);
                NimbusSound.Play(NimbusSoundKind.Dizzy);
                break;
            case NimbusState.Question:
                Blink();
                break;
            case NimbusState.RateLimit:
                Emit("sweat", 1);
                break;
            default:
                if (prev != NimbusState.Idle || next != NimbusState.Idle) Blink();
                break;
        }
    }

    public void SetLook(double x, double y, bool holdBriefly = true)
    {
        _lookX = Math.Clamp(x, -1, 1);
        _lookY = Math.Clamp(y, -1, 1);
        if (holdBriefly) _externalLookUntil = NowSec() + 2.5;
    }

    public void Blink()
    {
        if (_locks.Contains(NimbusPropKey.Open)) return;
        Anim(NimbusPropKey.Open, [new(0.06, 70, NimbusEase.InOut), new(1, 130, NimbusEase.Out)]);
    }

    public void Squash()
    {
        _physVy += 0.6;
        Anim(NimbusPropKey.Sy, [new(0.78, 70, NimbusEase.Out), new(1.1, 130, NimbusEase.Out), new(1, 170, NimbusEase.InOut)]);
        Anim(NimbusPropKey.Sx, [new(1.16, 70, NimbusEase.Out), new(0.95, 130, NimbusEase.Out), new(1, 170, NimbusEase.InOut)]);
    }

    public void Gulp()
    {
        _slotHTarget = 0.42;
        _ = Task.Delay(460).ContinueWith(_ =>
        {
            _slotHTarget = 0;
            _isChewing = true;
            _ = Task.Delay(800).ContinueWith(__ => _isChewing = false);
        });
        Anim(NimbusPropKey.Sy, [new(0.78, 80, NimbusEase.Out), new(1.18, 130, NimbusEase.Out), new(1, 220, NimbusEase.Back)]);
        Anim(NimbusPropKey.Sx, [new(1.28, 80, NimbusEase.Out), new(0.92, 130, NimbusEase.Out), new(1, 220, NimbusEase.Back)]);
        Blink();
    }

    public void Slap()
    {
        InterruptGreet();
        if (State == NimbusState.Dizzy) return;
        double t = NowSec();
        _slapTimes.RemoveAll(s => t - s >= 1.7);
        _slapTimes.Add(t);
        Squash();
        _physVy -= 1.2;
        _physVx += _rng.NextDouble() < 0.5 ? 0.7 : -0.7;
        if (_slapTimes.Count >= 3)
        {
            _slapTimes.Clear();
            SetState(NimbusState.Dizzy, force: true);
            DizzyTriggered?.Invoke();
        }
        else
        {
            NimbusSound.Play(NimbusSoundKind.Slap);
            _eyeOverride = NimbusEyeShape.Line;
            _eyeOverrideUntil = t + 0.8;
        }
    }

    public void DoRoll(double durationMs, int turns)
    {
        _roll = 0;
        Anim(NimbusPropKey.Roll, [new(Math.PI * 2 * turns, durationMs, NimbusEase.InOut)], () => _roll = 0);
    }

    public void Greet()
    {
        double t = NowSec();
        int tok = ++_greetToken;
        _waveStart = t + 0.45;
        _waveUntil = t + 1.55;
        _physVx += 0.2;
        _eyeOverride = NimbusEyeShape.Happy;
        _eyeOverrideUntil = t + 2.0;
        Anim(NimbusPropKey.Oy, [new(-0.06, 220, NimbusEase.Out), new(0, 220, NimbusEase.Back)]);

        _ = Task.Delay(250).ContinueWith(_ =>
        {
            if (_greetToken != tok) return;
            Anim(NimbusPropKey.Hands, [new(1, 280, NimbusEase.Out)]);
            Anim(NimbusPropKey.Sy, [new(0.95, 100, NimbusEase.Out), new(1, 260, NimbusEase.Back)]);
            Anim(NimbusPropKey.Sx, [new(1.04, 100, NimbusEase.Out), new(1, 260, NimbusEase.Back)]);
            NimbusSound.Play(NimbusSoundKind.Greet);
        });
        _ = Task.Delay(550).ContinueWith(_ => { if (_greetToken == tok) Blink(); });
        _ = Task.Delay(1500).ContinueWith(_ => { if (_greetToken == tok) Blink(); });
        _ = Task.Delay(1550).ContinueWith(_ =>
        {
            if (_greetToken != tok) return;
            _waveUntil = 0;
            Anim(NimbusPropKey.Hands, [new(0, 200, NimbusEase.InOut)]);
        });
        _ = Task.Delay(1750).ContinueWith(_ =>
        {
            if (_greetToken != tok) return;
            _eyeOverride = NimbusEyeShape.Happy;
            _eyeOverrideUntil = NowSec() + 0.3;
        });
    }

    private void InterruptGreet()
    {
        if (_hands <= 0.01 && NowSec() >= _waveUntil) return;
        _greetToken++;
        _waveUntil = 0;
        _waveStart = 0;
        Anim(NimbusPropKey.Hands, [new(0, 150, NimbusEase.InOut)]);
    }

    public void TriggerEmote(NimbusEmote emote, double duration = 1.8)
    {
        double t = NowSec();
        _eyeOverride = emote switch
        {
            NimbusEmote.Love => NimbusEyeShape.Heart,
            NimbusEmote.Surprised => NimbusEyeShape.Dot,
            NimbusEmote.Proud => NimbusEyeShape.Star,
            NimbusEmote.Wink => NimbusEyeShape.Wink,
            NimbusEmote.Yawn => NimbusEyeShape.Tired,
            NimbusEmote.Happy => NimbusEyeShape.Happy,
            NimbusEmote.Annoyed => NimbusEyeShape.Line,
            _ => NimbusEyeShape.Happy
        };
        _eyeOverrideUntil = t + duration;

        switch (emote)
        {
            case NimbusEmote.Love:
                Anim(NimbusPropKey.Blush, [new(1, 300, NimbusEase.Out), new(1, (duration - 0.6) * 1000, NimbusEase.Linear), new(0, 300, NimbusEase.InOut)]);
                Emit("heart", 4);
                Anim(NimbusPropKey.Oy, [new(-0.1, 160, NimbusEase.Out), new(0, 300, NimbusEase.Back)]);
                break;
            case NimbusEmote.Surprised:
                Anim(NimbusPropKey.Oy, [new(-0.26, 140, NimbusEase.Out), new(0, 380, NimbusEase.Back)]);
                Anim(NimbusPropKey.Es, [new(1.25, 120, NimbusEase.Out), new(1, 500, NimbusEase.InOut)]);
                break;
            case NimbusEmote.Proud:
                Emit("star", 5);
                Anim(NimbusPropKey.Tilt, [new(-0.14, 220, NimbusEase.Out), new(-0.14, (duration - 0.5) * 1000, NimbusEase.Linear), new(0, 280, NimbusEase.InOut)]);
                Anim(NimbusPropKey.Blush, [new(0.7, 250, NimbusEase.Out), new(0.7, (duration - 0.5) * 1000, NimbusEase.Linear), new(0, 300, NimbusEase.InOut)]);
                break;
            case NimbusEmote.Wink:
                Anim(NimbusPropKey.Tilt, [new(0.12, 160, NimbusEase.Out), new(0.12, (duration - 0.4) * 1000, NimbusEase.Linear), new(0, 240, NimbusEase.InOut)]);
                break;
            case NimbusEmote.Yawn:
                Anim(NimbusPropKey.Sy, [new(1.12, 500, NimbusEase.InOut), new(1, 500, NimbusEase.InOut)]);
                Anim(NimbusPropKey.Sx, [new(0.94, 500, NimbusEase.InOut), new(1, 500, NimbusEase.InOut)]);
                _ = Task.Delay(700).ContinueWith(_ =>
                {
                    _eyeOverride = NimbusEyeShape.Closed;
                    Emit("z", 2);
                });
                break;
            case NimbusEmote.Happy:
                Anim(NimbusPropKey.Blush, [new(0.6, 200, NimbusEase.Out), new(0, 600, NimbusEase.InOut)]);
                break;
        }
    }

    public void Emit(string type, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bool isZ = type == "z";
            _particles.Add(new NimbusParticle
            {
                Type = type,
                X = (_rng.NextDouble() - 0.5) * 0.9 + (isZ ? 0.55 : 0),
                Y = -0.7 - _rng.NextDouble() * 0.2,
                Vx = (_rng.NextDouble() - 0.5) * 0.35 + (isZ ? 0.18 : 0),
                Vy = -(0.45 + _rng.NextDouble() * 0.35),
                Age = -i * 0.14,
                Life = 1.3 + _rng.NextDouble() * 0.5,
                Rot = _rng.NextDouble() * Math.PI * 2,
                Size = 0.15 + _rng.NextDouble() * 0.08
            });
        }
    }

    public void Tick()
    {
        double n = NowSec();
        double dt = Math.Min(0.05, Math.Max(0.001, n - _lastTime));
        _lastTime = n;

        StepTweens(NowMs());

        // Smooth color interpolation
        double ck = 1 - Math.Exp(-8 * dt);
        _col = MixColor(_col, _colT, ck);

        // Eye override expiry
        if (_eyeOverride is not null && n >= _eyeOverrideUntil)
        {
            _eyeOverride = _permanentEye;
            if (_permanentEye is not null) _eyeOverrideUntil = double.PositiveInfinity;
        }

        // Smooth 112-BPM music dance fade-in (0.3 s) and fade-out (0.5 s)
        double danceTarget = _isDancing ? 1.0 : 0.0;
        if (_dancingLevel < danceTarget) _dancingLevel = Math.Min(danceTarget, _dancingLevel + dt / 0.3);
        else if (_dancingLevel > danceTarget) _dancingLevel = Math.Max(danceTarget, _dancingLevel - dt / 0.5);

        // Smooth mailbox morph transition
        double mailboxTarget = _mailbox ? 1.0 : 0.0;
        if (_mailboxLevel < mailboxTarget) _mailboxLevel = Math.Min(mailboxTarget, _mailboxLevel + dt / 0.25);
        else if (_mailboxLevel > mailboxTarget) _mailboxLevel = Math.Max(mailboxTarget, _mailboxLevel - dt / 0.25);

        // Auto blink (suppressed while sleeping or dizzy; 22% chance of a double-blink)
        if (n >= _nextBlink)
        {
            if (State != NimbusState.Sleeping && State != NimbusState.Dizzy)
            {
                Blink();
                if (_rng.NextDouble() < 0.22)
                    _ = Task.Delay(230).ContinueWith(_ => Blink());
            }
            _nextBlink = n + 2.2 + _rng.NextDouble() * 3.2;
        }

        // State-driven gaze & motion (matching Coucou BotEngine.update)
        double elapsed = n - _t0;
        double ty = _lookX * 0.62;
        double tp = _lookY * 0.5;

        if (_cfg.Look is { } look)
        {
            ty = ty * 0.35 + look.Yaw * 0.55;
            tp = tp * 0.3 + look.Pitch * 0.5;
        }
        if (_cfg.Scans)
        {
            ty = Math.Sin(elapsed * 2.6) * 0.6;
            tp = -0.06;
        }
        if (State == NimbusState.Sleeping) { ty = 0; tp = -0.14; }
        if (State == NimbusState.Dizzy) { ty = Math.Sin(elapsed * 9) * 0.25; tp = 0; }

        if (IsMini && _cfg.Look is null && !_cfg.Scans && State != NimbusState.Sleeping && State != NimbusState.Dizzy)
        {
            if (n > _miniLookNextTime)
            {
                _miniLookX = -0.88 + _rng.NextDouble() * 1.76;
                _miniLookY = -0.55 + _rng.NextDouble() * 1.0;
                _miniLookNextTime = n + 0.5 + _rng.NextDouble() * 1.5;
            }
            ty = _miniLookX * 0.62;
            tp = _miniLookY * 0.5;
        }
        else if (State == NimbusState.Idle && !IsMini && n >= _nextAmbient && n >= _externalLookUntil && Math.Abs(_lookX) < 0.05 && Math.Abs(_lookY) < 0.05)
        {
            ty = (_rng.NextDouble() - 0.5) * 0.45;
            tp = (_rng.NextDouble() - 0.5) * 0.24;
            _nextAmbient = n + 2.4 + _rng.NextDouble() * 2.8;
            if (QuotaMood == NimbusQuotaMood.Energetic && _rng.NextDouble() < 0.25)
                TriggerEmote(NimbusEmote.Happy, 1.2);
        }

        _tgYaw = ty;
        _tgPitch = tp;
        _tgTilt = _cfg.Tilt;

        if (n > _waveStart && n < _waveUntil)
        {
            double wt = n - _waveStart;
            _tgTilt = -0.06 + Math.Sin(2 * Math.PI * 1.2 * wt) * 0.07;
        }

        double bounce = _cfg.Bounces ? -Math.Abs(Math.Sin(elapsed * 5.2)) * 0.07 : 0;
        double kGen = 1 - Math.Pow(0.0008, dt);
        if (!_locks.Contains(NimbusPropKey.Oy)) _oy += (bounce - _oy) * kGen;

        if (_cfg.Breathes)
        {
            double amp = IsMini ? 0.07 : 0.035;
            double breath = Math.Sin(elapsed * 1.8) * amp;
            _tgSy = 1 + breath;
            _tgSx = 1 - breath * 0.57;
        }
        else if (IsMini)
        {
            _tgSy = 1 + Math.Sin(elapsed * 2.2) * 0.04;
            _tgSx = 1 - Math.Sin(elapsed * 2.2) * 0.02;
        }
        else
        {
            _tgSy = 1;
            _tgSx = 1;
        }

        if (IsMini && n > _miniNextBehavior) DoMiniBehaviorLoop();

        // Periodic ambient particles
        if ((_cfg.Zz || _cfg.Sweat) && n >= _nextParticle)
        {
            if (_cfg.Zz) Emit("z", 1);
            if (_cfg.Sweat && !IsMini && _rng.NextDouble() < 0.5) Emit("sweat", 1);
            _nextParticle = n + (_cfg.Zz ? 1.3 : 1.5);
        }

        // Smooth follow for look / scale / tilt (respecting active tween locks)
        double kLook = 1 - Math.Pow(0.0025, dt);
        if (!_locks.Contains(NimbusPropKey.Yaw)) _yaw = Lerp(_yaw, _tgYaw, kLook);
        if (!_locks.Contains(NimbusPropKey.Pitch)) _pitch = Lerp(_pitch, _tgPitch, kLook);
        if (!_locks.Contains(NimbusPropKey.Tilt)) _tilt = Lerp(_tilt, _tgTilt, kGen);
        if (!_locks.Contains(NimbusPropKey.Sx)) _sx = Lerp(_sx, _tgSx, kGen);
        if (!_locks.Contains(NimbusPropKey.Sy)) _sy = Lerp(_sy, _tgSy, kGen);

        // Mouth slot spring (omega = 2pi / 0.25, zeta = 0.6)
        const double omega = (2 * Math.PI) / 0.25;
        const double zeta = 0.6;
        double acc = omega * omega * (_slotHTarget - _slotH) - 2 * zeta * omega * _slotHVel;
        _slotHVel += acc * dt;
        _slotH = Math.Max(0, _slotH + _slotHVel * dt);

        // Soft-part spring: lags behind head turns, hops, and rolls (stiffness 60, damping 9)
        if (dt > 0)
        {
            double yawVel = (_yaw - _prevYaw) / dt;
            double oyVel = (_oy - _prevOy) / dt;
            double dRoll = _roll - _prevRoll;
            double rollVel = Math.Abs(dRoll) > Math.PI ? 0 : dRoll / dt;
            bool rigidRoll = !IsMini && NimbusOutfits.ResolveOutfit(_outfit, DateTime.Now) != NimbusOutfit.None;
            double centrifugal = rigidRoll ? rollVel * 0.18 : 0;
            double tDx = Math.Clamp(-yawVel * 0.35 - _tilt * 2 + centrifugal, -1, 1);
            double tDy = Math.Clamp(oyVel * 0.5, -1, 1);
            _physVx += (60 * (tDx - _physDx) - 9 * _physVx) * dt;
            _physVy += (60 * (tDy - _physDy) - 9 * _physVy) * dt;
            _physDx += _physVx * dt;
            _physDy += _physVy * dt;
        }
        _prevYaw = _yaw;
        _prevOy = _oy;
        _prevRoll = _roll;

        // Step particles
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life) _particles.RemoveAt(i);
        }
    }

    public void Draw(DrawingContext dc, double width, double height)
    {
        double now = NowSec();
        double r = Math.Min(width, height) * (IsMini ? 0.38 : 0.36);
        double rx = r * 1.14;
        double ry = r * 0.88;
        double floatY = (!_isDancing && State != NimbusState.Dizzy)
            ? Math.Sin(now * 1.9) * r * (State == NimbusState.Sleeping ? 0.025 : 0.045)
            : 0;
        double cx = width / 2.0 + _ox * r;
        double cy = height / 2.0 + _oy * r + floatY;

        bool dance = _dancingLevel > 0.001;
        if (dance)
        {
            double px = cx;
            double py = cy + ry;
            double beat = (now * 112.0) / 60.0;
            double hop = Math.Abs(Math.Sin(Math.PI * beat));
            double land = Math.Pow(1.0 - hop, 6.0);
            double dx = 0.08 * r * Math.Sin(Math.PI * beat) * _dancingLevel;
            double dy = -0.20 * r * hop * _dancingLevel;
            double rotDeg = 0.10 * Math.Sin(Math.PI * beat) * _dancingLevel * 180.0 / Math.PI;
            double dsx = 1.0 + 0.045 * land * _dancingLevel;
            double dsy = 1.0 - 0.06 * land * _dancingLevel;

            dc.PushTransform(new TranslateTransform(px + dx, py + dy));
            dc.PushTransform(new RotateTransform(rotDeg));
            dc.PushTransform(new ScaleTransform(dsx, dsy));
            dc.PushTransform(new TranslateTransform(-px, -py));
        }

        if ((!IsMini || now < _launchGreetingUntil) && _hands > 0.01 && r > 7)
            DrawHandsBehind(dc, r, rx, ry, cx, cy);

        var resolvedOutfit = NimbusOutfits.ResolveOutfit(_outfit, DateTime.Now);
        double outfitAlpha = Math.Max(0.0, 1.0 - _mailboxLevel * 1.4);
        bool rigidRoll = !IsMini && resolvedOutfit != NimbusOutfit.None && outfitAlpha > 0.05;

        dc.PushTransform(new TranslateTransform(cx, cy));
        double totalTilt = _tilt + (rigidRoll ? _roll : 0);
        if (Math.Abs(totalTilt) > 0.001) dc.PushTransform(new RotateTransform(totalTilt * 180.0 / Math.PI));
        if (Math.Abs(_sx - 1) > 0.001 || Math.Abs(_sy - 1) > 0.001) dc.PushTransform(new ScaleTransform(_sx, _sy));

        NimbusOutfits.DrawBehind(dc, resolvedOutfit, r, _yaw, _pitch, outfitAlpha, _physDx, _physDy);

        var body = BuildBodyGeometry(rx, ry, _mailboxLevel);
        DrawBody(dc, body, r, rx, ry, NimbusOutfits.IsPumpkin(resolvedOutfit) && outfitAlpha > 0.1);
        DrawBlush(dc, body, r, rx, ry);
        DrawEyes(dc, body, r, rx, ry, rigidRoll ? 0 : _roll);
        DrawMouthSlot(dc, r, rx, ry);
        NimbusOutfits.DrawFront(dc, resolvedOutfit, r, _yaw, _pitch, outfitAlpha, _physDx, _physDy);

        if (Math.Abs(_sx - 1) > 0.001 || Math.Abs(_sy - 1) > 0.001) dc.Pop();
        if (Math.Abs(totalTilt) > 0.001) dc.Pop();
        dc.Pop();

        if (_badge is { } badge && _badgeS > 0.01)
            DrawBadge(dc, badge, r, cx, cy);

        // Update hint dot when idle or sleeping
        if (_updateHintColor is { } hintCol && (State == NimbusState.Idle || State == NimbusState.Sleeping))
        {
            double pulse = 0.65 + 0.35 * Math.Sin(now * 3.5);
            var dotBrush = new SolidColorBrush(Color.FromArgb((byte)(230 * pulse), hintCol.R, hintCol.G, hintCol.B));
            var ringPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(110 * pulse), hintCol.R, hintCol.G, hintCol.B)), Math.Max(1.0, r * 0.07));
            var pt = new Point(cx + rx * 0.72, cy - ry * 0.72);
            dc.DrawEllipse(dotBrush, ringPen, pt, Math.Max(2.2, r * 0.14), Math.Max(2.2, r * 0.14));
        }

        DrawParticles(dc, r, cx, cy);

        if (dance)
        {
            dc.Pop();
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }

    private static StreamGeometry BuildBodyGeometry(double rx, double ry, double mailboxLevel = 0)
    {
        const int n = 96;
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        for (int i = 0; i <= n; i++)
        {
            double a = (double)i / n * Math.PI * 2;
            double ca = Math.Cos(a);
            double sa = Math.Sin(a);

            // Fluffy 5-puff cloud contour with a gently flattened base.
            double lobe = 0.5 + 0.5 * Math.Cos(5.0 * (a + Math.PI / 2.0));
            double topMask = Math.Clamp((0.45 - sa) / 0.85, 0.0, 1.0);
            double puffScale = 1.0 + 0.16 * lobe * topMask;
            double cloudX = rx * 1.15 * ca * puffScale;
            double cloudY = ry * 0.94 * sa * puffScale;
            if (sa > 0.25)
            {
                double bottomT = Math.Clamp((sa - 0.25) / 0.75, 0.0, 1.0);
                cloudY = Lerp(cloudY, ry * (0.72 + 0.05 * sa), bottomT * 0.72);
            }

            double px = cloudX;
            double py = cloudY;
            if (mailboxLevel > 0.001)
            {
                double boxX = rx * 1.08 * (ca >= 0 ? Math.Pow(ca, 0.24) : -Math.Pow(-ca, 0.24));
                double boxY = ry * 0.94 * (sa >= 0 ? Math.Pow(sa, 0.24) : -Math.Pow(-sa, 0.24));
                px = Lerp(cloudX, boxX, mailboxLevel);
                py = Lerp(cloudY, boxY, mailboxLevel);
            }

            var pt = new Point(px, py);
            if (i == 0) ctx.BeginFigure(pt, true, true);
            else ctx.LineTo(pt, true, true);
        }
        geo.Freeze();
        return geo;
    }

    private void DrawBody(DrawingContext dc, Geometry body, double r, double rx, double ry, bool pumpkin = false)
    {
        var topCol = pumpkin ? Color.FromRgb(255, 169, 77) : BaseTop;
        var botCol = pumpkin ? Color.FromRgb(232, 89, 12) : BaseBottom;
        var baseGrad = new LinearGradientBrush(topCol, botCol, new Point(0.85, 0.08), new Point(0.1, 0.95));
        var rimPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 165, 198, 235)), Math.Max(0.8, r * 0.045));
        dc.DrawGeometry(baseGrad, rimPen, body);

        if (_tint > 0.01)
        {
            var tintTop = Color.FromArgb(0, _col.R, _col.G, _col.B);
            var tintBot = Color.FromArgb((byte)Math.Round(255 * 0.72 * _tint), _col.R, _col.G, _col.B);
            var tintGrad = new LinearGradientBrush(tintBot, tintTop, new Point(0.5, 1), new Point(0.5, 0));
            dc.DrawGeometry(tintGrad, null, body);
        }

        // Subtle bottom cloud shadow
        var vig = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.65,
            RadiusY = 0.65
        };
        vig.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.58));
        vig.GradientStops.Add(new GradientStop(Color.FromArgb(40, 30, 58, 95), 1.0));
        dc.DrawGeometry(vig, null, body);

        // Specular highlight
        var hl = new RadialGradientBrush(Color.FromArgb(140, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
        {
            Center = new Point(0.68, 0.24),
            GradientOrigin = new Point(0.68, 0.24),
            RadiusX = 0.26,
            RadiusY = 0.24
        };
        dc.DrawGeometry(hl, null, body);
    }

    private void DrawBlush(DrawingContext dc, Geometry body, double r, double rx, double ry)
    {
        double blushVal = Math.Max(_blush, _tint * 0.45);
        if (blushVal <= 0.02) return;
        dc.PushClip(body);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(128 * blushVal, 0, 255), 255, 148, 168));
        double yOffset = Math.Sin(_yaw) * rx * 0.8;
        foreach (int sd in new[] { -1, 1 })
            dc.DrawEllipse(brush, null, new Point(sd * rx * 0.52 + yOffset, ry * 0.22), r * 0.16, r * 0.095);
        dc.Pop();
    }

    private void DrawEyes(DrawingContext dc, Geometry body, double r, double rx, double ry, double eyeRoll = 0)
    {
        var shape = _mailboxLevel > 0.5
            ? NimbusEyeShape.Cup
            : _eyeOverride ?? (_isDancing && _dancingLevel > 0.15 && !IsMini && (State == NimbusState.Idle || State == NimbusState.Finished)
                ? NimbusEyeShape.Happy
                : _cfg.Eye);
        dc.PushClip(body);
        var ink = new SolidColorBrush(IsMini ? MiniInkColor : InkColor);
        double eyeMult = IsMini ? 1.85 : 1.0;

        foreach (int sd in new[] { -1, 1 })
        {
            double eyeYaw = sd * EyeSp + _yaw;
            double eyePitch = EyeP + _pitch + eyeRoll;
            eyePitch = (((eyePitch + Math.PI) % (Math.PI * 2)) + Math.PI * 2) % (Math.PI * 2) - Math.PI;
            double cp = Math.Cos(eyePitch);
            if (Math.Cos(eyeYaw) * cp <= 0.04) continue;

            double ex = Math.Sin(eyeYaw) * cp * rx;
            double ey = -Math.Sin(eyePitch) * ry;
            double fx = Math.Max(0.18, Math.Cos(eyeYaw));
            double fy = Math.Max(0.18, cp);
            double ew = r * EyeW * _es * eyeMult;
            double eh = r * EyeH * _es * eyeMult;

            dc.PushTransform(new TranslateTransform(ex, ey));
            dc.PushTransform(new ScaleTransform(fx, fy));
            DrawEyeShape(dc, shape, ew, eh, sd, ink);
            dc.Pop();
            dc.Pop();
        }
        dc.Pop();
    }

    private void DrawEyeShape(DrawingContext dc, NimbusEyeShape shape, double w, double h, int sd, Brush ink)
    {
        double t = NowSec();
        switch (shape)
        {
            case NimbusEyeShape.Wide:
                DrawEyeShape(dc, NimbusEyeShape.Pill, w * 1.16, h * 1.12, sd, ink);
                break;
            case NimbusEyeShape.Pill:
            {
                double hh = Math.Max(h * _open, w * 0.3);
                double rad = Math.Min(w / 2, hh / 2);
                dc.DrawRoundedRectangle(ink, null, new Rect(-w / 2, -hh / 2, w, hh), rad, rad);
                if (_open > 0.45)
                {
                    double glint = w * (IsMini ? 0.14 : 0.18);
                    dc.DrawEllipse(Brushes.White, null, new Point(w * 0.16, -hh * 0.18), glint, glint);
                }
                break;
            }
            case NimbusEyeShape.Dot:
            {
                dc.DrawEllipse(ink, null, new Point(0, 0), w * 0.45, w * 0.45);
                double glint = w * (IsMini ? 0.14 : 0.18);
                dc.DrawEllipse(Brushes.White, null, new Point(w * 0.14, -w * 0.16), glint, glint);
                break;
            }
            case NimbusEyeShape.Line:
                dc.PushTransform(new RotateTransform(-sd * 11.5));
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.78, -w * 0.21, w * 1.56, w * 0.42), w * 0.21, w * 0.21);
                dc.Pop();
                break;
            case NimbusEyeShape.Flat:
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.72, -w * 0.2, w * 1.44, w * 0.4), w * 0.2, w * 0.2);
                break;
            case NimbusEyeShape.Happy:
            {
                var pen = new Pen(ink, Math.Max(1.0, w * 0.48)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    double rx = w * 0.78;
                    ctx.BeginFigure(new Point(-rx, h * 0.14), false, false);
                    ctx.ArcTo(new Point(rx, h * 0.14), new Size(rx, rx * 0.9), 0, false, SweepDirection.Clockwise, true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                break;
            }
            case NimbusEyeShape.Closed:
            {
                var pen = new Pen(ink, Math.Max(1.0, w * 0.34)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    double rx = w * 0.72;
                    ctx.BeginFigure(new Point(-rx, -h * 0.04), false, false);
                    ctx.ArcTo(new Point(rx, -h * 0.04), new Size(rx, rx * 0.75), 0, false, SweepDirection.Counterclockwise, true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                break;
            }
            case NimbusEyeShape.Spiral:
            {
                var pen = new Pen(ink, Math.Max(1.0, w * 0.22)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    for (double a = 0; a < 4.4 * Math.PI; a += 0.28)
                    {
                        double rad = w * 0.06 + a * w * 0.056;
                        double aa = a + t * 9 * sd;
                        var pt = new Point(Math.Cos(aa) * rad, Math.Sin(aa) * rad);
                        if (a == 0) ctx.BeginFigure(pt, false, false);
                        else ctx.LineTo(pt, true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                break;
            }
            case NimbusEyeShape.Heart:
                DrawHeart(dc, new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6D)), w * 1.15);
                break;
            case NimbusEyeShape.Star:
                dc.PushTransform(new RotateTransform(t * 85 * sd));
                DrawStar(dc, new SolidColorBrush(Color.FromRgb(0xF7, 0xB3, 0x2B)), w * 1.05, w * 0.46);
                dc.Pop();
                break;
            case NimbusEyeShape.Tired:
                dc.DrawRoundedRectangle(ink, null, new Rect(-w / 2, -h * 0.02, w, h * 0.38), w / 2, w / 2);
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.62, -h * 0.1, w * 1.24, w * 0.22), w * 0.11, w * 0.11);
                break;
            case NimbusEyeShape.Wink:
                if (sd < 0) DrawEyeShape(dc, NimbusEyeShape.Pill, w, h, sd, ink);
                else DrawEyeShape(dc, NimbusEyeShape.Happy, w, h, sd, ink);
                break;
            case NimbusEyeShape.Cup:
                DrawEyeShape(dc, NimbusEyeShape.Pill, w, h, sd, ink);
                break;
        }
    }

    private void DrawHandsBehind(DrawingContext dc, double r, double rx, double ry, double cx, double cy)
    {
        double n = NowSec();
        double bodyH = 2 * ry;
        double hew = 0.3 * ry * _hands;
        double heh = 0.26 * ry * _hands;
        double hwB = rx * _sx;
        double hhB = ry * _sy;
        bool isWaving = n >= _waveStart && _waveStart > 0 && n < _waveUntil;

        var handGrad = new LinearGradientBrush(BaseTop, BaseBottom, new Point(0.85, 0.1), new Point(0.1, 0.9));
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)), 1);

        foreach (int sd in new[] { -1, 1 })
        {
            double localX, localY, handRot = 0;
            if (sd > 0 && isWaving)
            {
                double wt = n - _waveStart;
                double rise = Math.Min(1, wt / 0.18);
                double riseEased = 1 - Math.Pow(1 - rise, 3);
                double restX = hwB * 1.08, restY = hhB * 0.7;
                double waveX = hwB * 1.1 + Math.Cos(13 * wt) * 0.06 * bodyH;
                double waveY = -hhB * 0.15 - Math.Sin(13 * wt) * 0.14 * bodyH;
                localX = Lerp(restX, waveX, riseEased);
                localY = Lerp(restY, waveY, riseEased);
                handRot = (-0.5 + Math.Sin(13 * wt) * 0.35) * riseEased;
            }
            else
            {
                localX = sd * hwB * 1.08;
                localY = hhB * 0.7;
            }

            double cosT = Math.Cos(_tilt), sinT = Math.Sin(_tilt);
            double wx = cx + cosT * localX - sinT * localY;
            double wy = cy + sinT * localX + cosT * localY;

            dc.PushTransform(new TranslateTransform(wx, wy));
            if (Math.Abs(handRot) > 0.001) dc.PushTransform(new RotateTransform(handRot * 180.0 / Math.PI));
            dc.DrawEllipse(handGrad, outline, new Point(0, 0), hew, heh);
            if (Math.Abs(handRot) > 0.001) dc.Pop();
            dc.Pop();
        }
    }

    private void DrawBadge(DrawingContext dc, NimbusBadge badge, double r, double cx, double cy)
    {
        double bs = _badgeS * (IsMini ? 1.25 : 1.0);
        double bx = cx - r * 0.72 * _sx;
        double by = cy - r * 0.72 * _sy;
        double t = NowSec();

        dc.PushTransform(new TranslateTransform(bx, by));
        dc.PushTransform(new ScaleTransform(bs, bs));

        var fill = new SolidColorBrush(badge.Color);
        var black = Brushes.Black;

        if (badge.Kind == NimbusBadgeKind.Dots)
        {
            if (IsMini)
            {
                double phase = (t * 2.4) % 1;
                double dotR = r * 0.22 * (1 + 0.25 * Math.Sin(phase * Math.PI * 2));
                dc.DrawEllipse(black, null, new Point(0, 0), r * 0.26, r * 0.26);
                dc.DrawEllipse(fill, null, new Point(0, 0), dotR, dotR);
            }
            else
            {
                double pw = r * 0.72, ph = r * 0.36;
                dc.DrawRoundedRectangle(fill, null, new Rect(-pw / 2, -ph / 2, pw, ph), ph / 2, ph / 2);
                for (int i = 0; i < 3; i++)
                {
                    double phase = (((t * 2.4 - i * 0.22) % 1) + 1) % 1;
                    double dotR = r * 0.055 * (1 + 0.4 * Math.Max(0, Math.Sin(phase * Math.PI * 2)));
                    dc.DrawEllipse(Brushes.White, null, new Point((i - 1) * r * 0.18, 0), dotR, dotR);
                }
            }
        }
        else if (badge.Kind is NimbusBadgeKind.Bang or NimbusBadgeKind.Question)
        {
            dc.DrawEllipse(black, null, new Point(0, 0), r * 0.3, r * 0.3);
            dc.DrawEllipse(fill, null, new Point(0, 0), r * 0.23, r * 0.23);
            if (!IsMini)
            {
                var ft = new FormattedText(
                    badge.Kind == NimbusBadgeKind.Bang ? "!" : "?",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                    Math.Max(6, r * 0.32),
                    Brushes.White,
                    1.0);
                dc.DrawText(ft, new Point(-ft.Width / 2, -ft.Height / 2));
            }
        }
        else
        {
            dc.DrawEllipse(black, null, new Point(0, 0), r * 0.2, r * 0.2);
            dc.DrawEllipse(fill, null, new Point(0, 0), r * 0.135, r * 0.135);
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawParticles(DrawingContext dc, double r, double cx, double cy)
    {
        foreach (var p in _particles)
        {
            if (p.Age <= 0) continue;
            double k = p.Age / p.Life;
            double a = k < 0.2 ? k / 0.2 : 1 - (k - 0.2) / 0.8;
            byte alpha = (byte)Math.Clamp(a * 255, 0, 255);
            double px = cx + (p.X + p.Vx * p.Age) * r * 1.3;
            double py = cy + (p.Y + p.Vy * p.Age) * r * 1.3;
            double sz = r * p.Size * (1 + k * 0.4);

            dc.PushTransform(new TranslateTransform(px, py));
            switch (p.Type)
            {
                case "heart":
                    dc.PushTransform(new RotateTransform(Math.Sin(p.Age * 6) * 18));
                    DrawHeart(dc, new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0x4D, 0x6D)), sz);
                    dc.Pop();
                    break;
                case "star":
                    dc.PushTransform(new RotateTransform((p.Rot + p.Age * 2) * 180 / Math.PI));
                    DrawStar(dc, new SolidColorBrush(Color.FromArgb(alpha, 0xF7, 0xB3, 0x2B)), sz, sz * 0.45);
                    dc.Pop();
                    break;
                case "spark":
                    dc.PushTransform(new RotateTransform(p.Rot * 180 / Math.PI));
                    DrawStar(dc, new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), sz * 0.8, sz * 0.18);
                    dc.Pop();
                    break;
                case "sweat":
                    DrawSweat(dc, new SolidColorBrush(Color.FromArgb(alpha, 0x7C, 0xC7, 0xFF)), sz);
                    break;
                case "z":
                {
                    var ft = new FormattedText(
                        "z",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        Math.Max(6, sz * 1.9),
                        new SolidColorBrush(Color.FromArgb(alpha, 209, 219, 235)),
                        1.0);
                    dc.DrawText(ft, new Point(-ft.Width / 2, -ft.Height / 2));
                    break;
                }
            }
            dc.Pop();
        }
    }

    private static void DrawHeart(DrawingContext dc, Brush brush, double s)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        ctx.BeginFigure(new Point(0, s * 0.38), true, true);
        ctx.BezierTo(new Point(-s * 1.05, -s * 0.15), new Point(-s * 0.5, -s * 0.95), new Point(0, -s * 0.38), true, true);
        ctx.BezierTo(new Point(s * 0.5, -s * 0.95), new Point(s * 1.05, -s * 0.15), new Point(0, s * 0.38), true, true);
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawStar(DrawingContext dc, Brush brush, double ro, double ri)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        for (int i = 0; i < 10; i++)
        {
            double rad = i % 2 == 1 ? ri : ro;
            double a = -Math.PI / 2 + i * Math.PI / 5;
            var pt = new Point(Math.Cos(a) * rad, Math.Sin(a) * rad);
            if (i == 0) ctx.BeginFigure(pt, true, true);
            else ctx.LineTo(pt, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawSweat(DrawingContext dc, Brush brush, double sz)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        ctx.BeginFigure(new Point(0, -sz), true, true);
        ctx.QuadraticBezierTo(new Point(sz * 0.8, sz * 0.2), new Point(0, sz * 0.6), true, true);
        ctx.QuadraticBezierTo(new Point(-sz * 0.8, sz * 0.2), new Point(0, -sz), true, true);
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private void DrawMouthSlot(DrawingContext dc, double r, double rx, double ry)
    {
        double h = _slotH * r;
        if (h >= 0.5 || _isChewing)
        {
            double chew = _isChewing ? Math.Max(0, Math.Sin(NowSec() * 18)) * r * 0.08 : 0;
            double mouthH = Math.Max(1.0, h + chew);
            double mouthW = r * 0.52;
            double mx = _yaw * rx * 0.55;
            double my = ry * 0.34 + _pitch * ry * 0.3;
            dc.DrawRoundedRectangle(new SolidColorBrush(InkColor), null, new Rect(mx - mouthW / 2, my - mouthH / 2, mouthW, mouthH), mouthW / 2, mouthH / 2);
            return;
        }

        if (_mailboxLevel >= 0.4 || r < 6) return;

        double smx = _yaw * rx * 0.48;
        double smy = ry * 0.22 + _pitch * ry * 0.24;
        var ink = new SolidColorBrush(IsMini ? MiniInkColor : InkColor);

        if (State == NimbusState.Sleeping || _eyeOverride == NimbusEyeShape.Dot)
        {
            double rad = r * (IsMini ? 0.08 : 0.065);
            dc.DrawEllipse(ink, null, new Point(smx, smy + rad * 0.4), rad, rad);
            return;
        }

        double mw = r * (IsMini ? 0.22 : 0.18);
        var pen = new Pen(ink, Math.Max(1.0, r * (IsMini ? 0.09 : 0.065)))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (State is NimbusState.Error or NimbusState.RateLimit)
            {
                ctx.BeginFigure(new Point(smx - mw * 0.85, smy + mw * 0.15), false, false);
                ctx.QuadraticBezierTo(new Point(smx, smy - mw * 0.25), new Point(smx + mw * 0.85, smy + mw * 0.15), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(smx - mw, smy), false, false);
                ctx.QuadraticBezierTo(new Point(smx, smy + mw * 0.65), new Point(smx + mw, smy), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DoMiniBehaviorLoop()
    {
        double n = NowSec();
        switch (_permanentEmote)
        {
            case NimbusEmote.Happy:
                if (_locks.Contains(NimbusPropKey.Oy)) { _miniNextBehavior = n + 0.4; return; }
                Anim(NimbusPropKey.Oy, [new(-0.3, 120, NimbusEase.Out), new(0.03, 200, NimbusEase.InOut), new(0, 160, NimbusEase.Back)]);
                Anim(NimbusPropKey.Sy, [new(0.82, 80, NimbusEase.Out), new(1.18, 130, NimbusEase.Out), new(0.88, 160, NimbusEase.InOut), new(1, 200, NimbusEase.Back)]);
                Anim(NimbusPropKey.Sx, [new(1.15, 80, NimbusEase.Out), new(0.88, 130, NimbusEase.Out), new(1.06, 160, NimbusEase.InOut), new(1, 200, NimbusEase.Back)]);
                _miniNextBehavior = n + 2.2 + _rng.NextDouble() * 1.2;
                break;
            case NimbusEmote.Annoyed:
                if (_locks.Contains(NimbusPropKey.Yaw)) { _miniNextBehavior = n + 0.5; return; }
                Anim(NimbusPropKey.Yaw, [
                    new(-0.65, 50, NimbusEase.Out), new(0.65, 90, NimbusEase.InOut), new(-0.5, 80, NimbusEase.InOut),
                    new(0.4, 75, NimbusEase.InOut), new(-0.2, 70, NimbusEase.InOut), new(0, 140, NimbusEase.Out)
                ]);
                _miniNextBehavior = n + 3.0 + _rng.NextDouble() * 2.5;
                break;
            case NimbusEmote.Wink:
                _eyeOverride = NimbusEyeShape.Wink;
                _eyeOverrideUntil = n + 0.55;
                Anim(NimbusPropKey.Tilt, [new(0.13, 100, NimbusEase.Out), new(0.13, 320, NimbusEase.Linear), new(0, 200, NimbusEase.InOut)]);
                _miniNextBehavior = n + 2.2 + _rng.NextDouble() * 2.0;
                break;
            case NimbusEmote.Love:
                Emit("heart", 2);
                Anim(NimbusPropKey.Tilt, [new(-0.1, 180, NimbusEase.Out), new(0.1, 340, NimbusEase.InOut), new(0, 220, NimbusEase.InOut)]);
                _miniNextBehavior = n + 2.6 + _rng.NextDouble() * 1.5;
                break;
            default:
                _miniNextBehavior = n + 3.0 + _rng.NextDouble() * 2.0;
                break;
        }
    }

    // ── Tween Engine ──────────────────────────────────────────────────────────

    private void SetBadge(NimbusBadge? b)
    {
        string key = b is { } bd ? $"{bd.Kind}-{bd.Color}" : "none";
        if (key == _badgeKey) return;
        _badgeKey = key;
        int tok = ++_badgeToken;
        Anim(NimbusPropKey.BadgeS, [new(0, 90, NimbusEase.InOut)]);
        _ = Task.Delay(100).ContinueWith(_ =>
        {
            if (tok != _badgeToken) return;
            _badge = b;
            if (b is not null) Anim(NimbusPropKey.BadgeS, [new(1, 280, NimbusEase.Back)]);
        });
    }

    private void Anim(NimbusPropKey prop, NimbusTweenKey[] keys, Action? onComplete = null)
    {
        _tweens[prop] = new NimbusTween
        {
            Prop = prop,
            Keys = keys,
            Index = 0,
            From = GetProp(prop),
            StartMs = NowMs(),
            OnComplete = onComplete
        };
        _locks.Add(prop);
    }

    private void StepTweens(double nowMs)
    {
        if (_tweens.Count == 0) return;
        foreach (var prop in _tweens.Keys.ToList())
        {
            if (!_tweens.TryGetValue(prop, out var tw)) continue;
            var key = tw.Keys[tw.Index];
            double t = key.DurationMs <= 0 ? 1 : Math.Clamp((nowMs - tw.StartMs) / key.DurationMs, 0, 1);
            SetProp(prop, Lerp(tw.From, key.Target, ApplyEase(key.Ease, t)));
            if (t >= 1)
            {
                tw.Index++;
                if (tw.Index >= tw.Keys.Length)
                {
                    _tweens.Remove(prop);
                    _locks.Remove(prop);
                    tw.OnComplete?.Invoke();
                }
                else
                {
                    tw.From = key.Target;
                    tw.StartMs = nowMs;
                }
            }
        }
    }

    private double GetProp(NimbusPropKey p) => p switch
    {
        NimbusPropKey.Yaw => _yaw, NimbusPropKey.Pitch => _pitch, NimbusPropKey.Roll => _roll,
        NimbusPropKey.Tilt => _tilt, NimbusPropKey.Open => _open, NimbusPropKey.Sx => _sx,
        NimbusPropKey.Sy => _sy, NimbusPropKey.Oy => _oy, NimbusPropKey.Ox => _ox,
        NimbusPropKey.Tint => _tint, NimbusPropKey.Hands => _hands, NimbusPropKey.Blush => _blush,
        NimbusPropKey.Es => _es, NimbusPropKey.BadgeS => _badgeS,
        _ => 0
    };

    private void SetProp(NimbusPropKey p, double v)
    {
        switch (p)
        {
            case NimbusPropKey.Yaw: _yaw = v; break;
            case NimbusPropKey.Pitch: _pitch = v; break;
            case NimbusPropKey.Roll: _roll = v; break;
            case NimbusPropKey.Tilt: _tilt = v; break;
            case NimbusPropKey.Open: _open = v; break;
            case NimbusPropKey.Sx: _sx = v; break;
            case NimbusPropKey.Sy: _sy = v; break;
            case NimbusPropKey.Oy: _oy = v; break;
            case NimbusPropKey.Ox: _ox = v; break;
            case NimbusPropKey.Tint: _tint = v; break;
            case NimbusPropKey.Hands: _hands = v; break;
            case NimbusPropKey.Blush: _blush = v; break;
            case NimbusPropKey.Es: _es = v; break;
            case NimbusPropKey.BadgeS: _badgeS = v; break;
        }
    }

    private static double ApplyEase(NimbusEase ease, double t) => ease switch
    {
        NimbusEase.Linear => t,
        NimbusEase.Out => 1 - Math.Pow(1 - t, 3),
        NimbusEase.InOut => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
        NimbusEase.Back => 1 + 2.70158 * Math.Pow(t - 1, 3) + 1.70158 * Math.Pow(t - 1, 2),
        _ => t
    };

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Color MixColor(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(Lerp(a.R, b.R, t)),
        (byte)Math.Round(Lerp(a.G, b.G, t)),
        (byte)Math.Round(Lerp(a.B, b.B, t)));
}
