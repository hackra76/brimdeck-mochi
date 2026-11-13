using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public enum MochiEyeShape
{
    Pill, Wide, Dot, Line, Flat, Happy, Closed,
    Spiral, Heart, Star, Tired, Wink, Cup
}

public enum MochiBadgeKind
{
    Dots, Bang, Question, Dot, Arrow
}

public readonly record struct MochiBadge(MochiBadgeKind Kind, Color Color);

internal enum MochiPropKey
{
    Yaw, Pitch, Roll, Tilt, Open, Sx, Sy,
    Oy, Ox, Tint, Hands, Blush, Es, BadgeS
}

internal enum MochiEase { Linear, Out, InOut, Back }

internal readonly record struct MochiTweenKey(double Target, double DurationMs, MochiEase Ease);

internal sealed class MochiTween
{
    public MochiPropKey Prop { get; init; }
    public MochiTweenKey[] Keys { get; init; } = [];
    public int Index { get; set; }
    public double From { get; set; }
    public double StartMs { get; set; }
    public Action? OnComplete { get; init; }
}

internal sealed class MochiParticle
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

internal sealed record MochiStateCfg(
    Color Color,
    double Tint,
    MochiEyeShape Eye,
    MochiBadge? Badge,
    bool Bounces = false,
    bool Scans = false,
    bool Breathes = false,
    bool Zz = false,
    bool Sweat = false,
    (double Yaw, double Pitch)? Look = null,
    double Tilt = 0);

/// <summary>
/// Procedural vector animation engine for Mochi, ported from Coucou's BotEngine
/// to native WPF DrawingContext rendering.
/// </summary>
public sealed class MochiEngine
{
    private const double EyeW = 0.25;
    private const double EyeH = 0.27;
    private const double EyeSp = 0.37;
    private const double EyeP = -0.12;

    private static readonly Color BaseTop = Color.FromRgb(0xED, 0xED, 0xEF);
    private static readonly Color BaseBottom = Color.FromRgb(0xC4, 0xC5, 0xCA);
    private static readonly Color InkColor = Color.FromRgb(26, 20, 18);
    private static readonly Color MiniInkColor = Color.FromRgb(16, 19, 26);

    private static readonly Dictionary<MochiState, MochiStateCfg> StateConfigs = new()
    {
        [MochiState.Idle] = new(Color.FromRgb(230, 233, 238), 0.00, MochiEyeShape.Pill, null),
        [MochiState.Working] = new(Color.FromRgb(59, 158, 255), 0.72, MochiEyeShape.Pill, new(MochiBadgeKind.Dots, Color.FromRgb(59, 158, 255))),
        [MochiState.Thinking] = new(Color.FromRgb(139, 92, 246), 0.72, MochiEyeShape.Pill, new(MochiBadgeKind.Dots, Color.FromRgb(139, 92, 246)), Look: (0.55, 0.55)),
        [MochiState.Searching] = new(Color.FromRgb(99, 102, 241), 0.72, MochiEyeShape.Pill, new(MochiBadgeKind.Dots, Color.FromRgb(99, 102, 241)), Scans: true),
        [MochiState.Approval] = new(Color.FromRgb(245, 165, 36), 0.78, MochiEyeShape.Wide, new(MochiBadgeKind.Bang, Color.FromRgb(245, 165, 36)), Bounces: true),
        [MochiState.Question] = new(Color.FromRgb(34, 211, 238), 0.75, MochiEyeShape.Pill, new(MochiBadgeKind.Question, Color.FromRgb(34, 211, 238)), Tilt: 0.17),
        [MochiState.Error] = new(Color.FromRgb(244, 80, 94), 0.78, MochiEyeShape.Flat, new(MochiBadgeKind.Dot, Color.FromRgb(244, 80, 94))),
        [MochiState.Finished] = new(Color.FromRgb(52, 211, 153), 0.35, MochiEyeShape.Happy, new(MochiBadgeKind.Dot, Color.FromRgb(52, 211, 153))),
        [MochiState.RateLimit] = new(Color.FromRgb(251, 146, 60), 0.72, MochiEyeShape.Tired, new(MochiBadgeKind.Dot, Color.FromRgb(251, 146, 60)), Sweat: true),
        [MochiState.Sleeping] = new(Color.FromRgb(148, 163, 184), 0.32, MochiEyeShape.Closed, null, Breathes: true, Zz: true),
        [MochiState.Dizzy] = new(Color.FromRgb(244, 114, 182), 0.70, MochiEyeShape.Spiral, null),
    };

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double NowSec() => Clock.Elapsed.TotalSeconds;
    private static double NowMs() => Clock.Elapsed.TotalMilliseconds;

    private readonly Random _rng = new();
    private readonly Dictionary<MochiPropKey, MochiTween> _tweens = new();
    private readonly List<MochiParticle> _particles = new();
    private readonly List<double> _slapTimes = new();

    public bool IsMini { get; set; }
    public MochiState State { get; private set; } = MochiState.Idle;
    public MochiQuotaMood QuotaMood { get; set; } = MochiQuotaMood.Normal;
    public event Action? DizzyTriggered;

    // Animated properties
    private double _yaw, _pitch, _roll, _tilt, _open = 1;
    private double _sx = 1, _sy = 1, _oy, _ox;
    private double _tint, _hands, _blush, _es = 1, _badgeS;

    // Targets
    private double _tgYaw, _tgPitch, _tgTilt, _tgSx = 1, _tgSy = 1;
    private Color _col = StateConfigs[MochiState.Idle].Color;
    private Color _colT = StateConfigs[MochiState.Idle].Color;
    private MochiStateCfg _cfg = StateConfigs[MochiState.Idle];

    private MochiEyeShape? _eyeOverride;
    private double _eyeOverrideUntil;
    private MochiBadge? _badge;
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
    private MochiOutfit _outfit = MochiOutfit.Auto;
    private bool _mailbox;
    private double _mailboxLevel;
    private Color? _updateHintColor;
    private double _launchGreetingStart;
    private double _launchGreetingUntil;

    public void SetDancing(bool dancing) => _isDancing = dancing;
    public void SetOutfit(MochiOutfit outfit) => _outfit = outfit;
    public void SetMailbox(bool mailbox) => _mailbox = mailbox;
    public void SetUpdateHint(Color? color) => _updateHintColor = color;

    public void StartLaunchGreeting()
    {
        double t = NowSec();
        _launchGreetingStart = t;
        _launchGreetingUntil = t + 2.4;
        Emit("spark", 4);
        Greet();
    }

    public void Poke()
    {
        Squash();
        MochiSound.Play(MochiSoundKind.Poke);
        TriggerEmote(MochiEmote.Happy, 1.1);
    }

    public void SetState(MochiState next, bool force = false)
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
            case MochiState.Finished:
                DoRoll(950, 1);
                Emit("spark", 5);
                MochiSound.Play(MochiSoundKind.Finished);
                break;
            case MochiState.Error:
                Anim(MochiPropKey.Ox, [new(0.08, 50, MochiEase.Out), new(-0.08, 70, MochiEase.InOut), new(0.05, 70, MochiEase.InOut), new(0, 90, MochiEase.Out)]);
                MochiSound.Play(MochiSoundKind.Error);
                break;
            case MochiState.Approval:
                Anim(MochiPropKey.Oy, [new(-0.2, 150, MochiEase.Out), new(0, 300, MochiEase.Back)]);
                MochiSound.Play(MochiSoundKind.Approval);
                break;
            case MochiState.Dizzy:
                DoRoll(1300, 2);
                MochiSound.Play(MochiSoundKind.Dizzy);
                break;
            case MochiState.Question:
                Blink();
                break;
            case MochiState.RateLimit:
                Emit("sweat", 1);
                break;
            default:
                if (prev != MochiState.Idle || next != MochiState.Idle) Blink();
                break;
        }
    }

    public void SetLook(double x, double y, bool holdBriefly = true)
    {
        _tgYaw = Math.Clamp(x, -1, 1) * 0.48;
        _tgPitch = Math.Clamp(y, -1, 1) * 0.36;
        if (holdBriefly) _externalLookUntil = NowSec() + 2.5;
    }

    public void Blink()
    {
        Anim(MochiPropKey.Open, [new(0.06, 70, MochiEase.InOut), new(1, 130, MochiEase.Out)]);
    }

    public void Squash()
    {
        Anim(MochiPropKey.Sy, [new(0.78, 70, MochiEase.Out), new(1.1, 130, MochiEase.Out), new(1, 170, MochiEase.InOut)]);
        Anim(MochiPropKey.Sx, [new(1.16, 70, MochiEase.Out), new(0.95, 130, MochiEase.Out), new(1, 170, MochiEase.InOut)]);
    }

    public void Slap()
    {
        InterruptGreet();
        if (State == MochiState.Dizzy) return;
        double t = NowSec();
        _slapTimes.RemoveAll(s => t - s >= 1.7);
        _slapTimes.Add(t);
        Squash();
        if (_slapTimes.Count >= 3)
        {
            _slapTimes.Clear();
            SetState(MochiState.Dizzy, force: true);
            DizzyTriggered?.Invoke();
        }
        else
        {
            MochiSound.Play(MochiSoundKind.Slap);
            _eyeOverride = MochiEyeShape.Line;
            _eyeOverrideUntil = t + 0.8;
        }
    }

    public void DoRoll(double durationMs, int turns)
    {
        _roll = 0;
        Anim(MochiPropKey.Roll, [new(Math.PI * 2 * turns, durationMs, MochiEase.InOut)], () => _roll = 0);
    }

    public void Greet()
    {
        double t = NowSec();
        int tok = ++_greetToken;
        _waveStart = t + 0.25;
        _waveUntil = t + 1.55;
        _eyeOverride = MochiEyeShape.Happy;
        _eyeOverrideUntil = t + 1.9;
        MochiSound.Play(MochiSoundKind.Greet);
        Anim(MochiPropKey.Oy, [new(-0.08, 200, MochiEase.Out), new(0, 220, MochiEase.Back)]);
        Anim(MochiPropKey.Hands, [new(1, 240, MochiEase.Out)]);
        Anim(MochiPropKey.Sy, [new(0.94, 100, MochiEase.Out), new(1, 260, MochiEase.Back)]);
        Anim(MochiPropKey.Sx, [new(1.06, 100, MochiEase.Out), new(1, 260, MochiEase.Back)]);

        _ = Task.Delay(1550).ContinueWith(_ =>
        {
            if (_greetToken == tok)
            {
                _waveUntil = 0;
                Anim(MochiPropKey.Hands, [new(0, 200, MochiEase.InOut)]);
            }
        });
    }

    private void InterruptGreet()
    {
        if (_hands <= 0.01 && NowSec() >= _waveUntil) return;
        _greetToken++;
        _waveUntil = 0;
        _waveStart = 0;
        Anim(MochiPropKey.Hands, [new(0, 150, MochiEase.InOut)]);
    }

    public void TriggerEmote(MochiEmote emote, double duration = 1.8)
    {
        double t = NowSec();
        _eyeOverride = emote switch
        {
            MochiEmote.Love => MochiEyeShape.Heart,
            MochiEmote.Surprised => MochiEyeShape.Dot,
            MochiEmote.Proud => MochiEyeShape.Star,
            MochiEmote.Wink => MochiEyeShape.Wink,
            MochiEmote.Yawn => MochiEyeShape.Tired,
            MochiEmote.Happy => MochiEyeShape.Happy,
            MochiEmote.Annoyed => MochiEyeShape.Line,
            _ => MochiEyeShape.Happy
        };
        _eyeOverrideUntil = t + duration;

        switch (emote)
        {
            case MochiEmote.Love:
                Anim(MochiPropKey.Blush, [new(1, 300, MochiEase.Out), new(1, (duration - 0.6) * 1000, MochiEase.Linear), new(0, 300, MochiEase.InOut)]);
                Emit("heart", 4);
                Anim(MochiPropKey.Oy, [new(-0.1, 160, MochiEase.Out), new(0, 300, MochiEase.Back)]);
                break;
            case MochiEmote.Surprised:
                Anim(MochiPropKey.Oy, [new(-0.26, 140, MochiEase.Out), new(0, 380, MochiEase.Back)]);
                Anim(MochiPropKey.Es, [new(1.25, 120, MochiEase.Out), new(1, 500, MochiEase.InOut)]);
                break;
            case MochiEmote.Proud:
                Emit("star", 5);
                Anim(MochiPropKey.Tilt, [new(-0.14, 220, MochiEase.Out), new(-0.14, (duration - 0.5) * 1000, MochiEase.Linear), new(0, 280, MochiEase.InOut)]);
                Anim(MochiPropKey.Blush, [new(0.7, 250, MochiEase.Out), new(0.7, (duration - 0.5) * 1000, MochiEase.Linear), new(0, 300, MochiEase.InOut)]);
                break;
            case MochiEmote.Wink:
                Anim(MochiPropKey.Tilt, [new(0.12, 160, MochiEase.Out), new(0.12, (duration - 0.4) * 1000, MochiEase.Linear), new(0, 240, MochiEase.InOut)]);
                break;
            case MochiEmote.Yawn:
                Anim(MochiPropKey.Sy, [new(1.12, 500, MochiEase.InOut), new(1, 500, MochiEase.InOut)]);
                Anim(MochiPropKey.Sx, [new(0.94, 500, MochiEase.InOut), new(1, 500, MochiEase.InOut)]);
                break;
            case MochiEmote.Happy:
                Anim(MochiPropKey.Blush, [new(0.6, 200, MochiEase.Out), new(0, 600, MochiEase.InOut)]);
                break;
        }
    }

    public void Emit(string type, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bool isZ = type == "z";
            _particles.Add(new MochiParticle
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
            _eyeOverride = null;

        // Smooth 112-BPM music dance fade-in (0.3 s) and fade-out (0.5 s)
        double danceTarget = _isDancing ? 1.0 : 0.0;
        if (_dancingLevel < danceTarget) _dancingLevel = Math.Min(danceTarget, _dancingLevel + dt / 0.3);
        else if (_dancingLevel > danceTarget) _dancingLevel = Math.Max(danceTarget, _dancingLevel - dt / 0.5);

        // Smooth mailbox morph transition
        double mailboxTarget = _mailbox ? 1.0 : 0.0;
        if (_mailboxLevel < mailboxTarget) _mailboxLevel = Math.Min(mailboxTarget, _mailboxLevel + dt / 0.25);
        else if (_mailboxLevel > mailboxTarget) _mailboxLevel = Math.Max(mailboxTarget, _mailboxLevel - dt / 0.25);

        // Auto blink
        if (n >= _nextBlink && _cfg.Eye != MochiEyeShape.Closed)
        {
            Blink();
            _nextBlink = n + 2.2 + _rng.NextDouble() * 3.0;
        }

        // State-driven motion
        double elapsed = n - _t0;
        if (_cfg.Look is { } look)
        {
            _tgYaw = look.Yaw;
            _tgPitch = look.Pitch;
        }
        else if (_cfg.Scans)
        {
            _tgYaw = Math.Sin(elapsed * 2.2) * 0.52;
            _tgPitch = Math.Cos(elapsed * 1.4) * 0.16;
        }
        else if (State == MochiState.Idle && n >= _nextAmbient && n >= _externalLookUntil)
        {
            _tgYaw = (_rng.NextDouble() - 0.5) * 0.45;
            _tgPitch = (_rng.NextDouble() - 0.5) * 0.24;
            _nextAmbient = n + 2.4 + _rng.NextDouble() * 2.8;
            if (QuotaMood == MochiQuotaMood.Energetic && _rng.NextDouble() < 0.25 && !IsMini)
                TriggerEmote(MochiEmote.Happy, 1.2);
        }

        if (_cfg.Bounces && !_tweens.ContainsKey(MochiPropKey.Oy))
            _oy = -Math.Abs(Math.Sin(elapsed * 4.5)) * 0.12;

        if (_cfg.Breathes && !_tweens.ContainsKey(MochiPropKey.Sy))
        {
            double breath = Math.Sin(elapsed * 1.8) * 0.035;
            _tgSy = 1 + breath;
            _tgSx = 1 - breath * 0.6;
        }
        else if (!_tweens.ContainsKey(MochiPropKey.Sy))
        {
            _tgSy = 1;
            _tgSx = 1;
        }

        // Periodic ambient particles
        if ((_cfg.Zz || _cfg.Sweat) && n >= _nextParticle)
        {
            if (_cfg.Zz) Emit("z", 1);
            if (_cfg.Sweat) Emit("sweat", 1);
            _nextParticle = n + (_cfg.Zz ? 1.4 : 1.8);
        }

        // Smooth follow for look / scale / tilt
        double k = 1 - Math.Exp(-10 * dt);
        _yaw = Lerp(_yaw, _tgYaw, k);
        _pitch = Lerp(_pitch, _tgPitch, k);
        if (!_tweens.ContainsKey(MochiPropKey.Tilt)) _tilt = Lerp(_tilt, _tgTilt, k);
        if (!_tweens.ContainsKey(MochiPropKey.Sx)) _sx = Lerp(_sx, _tgSx, k);
        if (!_tweens.ContainsKey(MochiPropKey.Sy)) _sy = Lerp(_sy, _tgSy, k);

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
        double r = Math.Min(width, height) * (IsMini ? 0.42 : 0.40);
        double rx = r * 1.14;
        double ry = r * 0.88;
        double cx = width / 2.0 + _ox * r;
        double cy = height / 2.0 + _oy * r + r * 0.06;

        // Launch drop-in greeting (Coucou drop + bounce + side peek)
        if (now < _launchGreetingUntil)
        {
            double elapsed = now - _launchGreetingStart;
            if (elapsed < 0.45)
            {
                double p = elapsed / 0.45;
                double drop = (1.0 - p) * (1.0 - p);
                cy -= height * 0.65 * drop;
            }
            else if (elapsed < 1.55)
            {
                double sway = Math.Sin(((elapsed - 0.45) / 1.10) * Math.PI);
                cx += r * 0.32 * sway;
            }
        }

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

        if (!IsMini && _hands > 0.01 && r > 10)
            DrawHandsBehind(dc, r, rx, ry, cx, cy);

        var resolvedOutfit = MochiOutfits.ResolveOutfit(_outfit, DateTime.Now);
        double outfitAlpha = Math.Max(0.0, 1.0 - _mailboxLevel * 1.4);

        dc.PushTransform(new TranslateTransform(cx, cy));
        if (Math.Abs(_tilt) > 0.001) dc.PushTransform(new RotateTransform(_tilt * 180.0 / Math.PI));
        if (Math.Abs(_sx - 1) > 0.001 || Math.Abs(_sy - 1) > 0.001) dc.PushTransform(new ScaleTransform(_sx, _sy));

        MochiOutfits.DrawBehind(dc, resolvedOutfit, r, _yaw, _pitch, outfitAlpha);

        var body = BuildBodyGeometry(rx, ry, _mailboxLevel);
        DrawBody(dc, body, r, rx, ry, MochiOutfits.IsPumpkin(resolvedOutfit) && outfitAlpha > 0.1);
        DrawBlush(dc, body, r, rx, ry);
        DrawEyes(dc, body, r, rx, ry);
        MochiOutfits.DrawFront(dc, resolvedOutfit, r, _yaw, _pitch, outfitAlpha);

        if (Math.Abs(_sx - 1) > 0.001 || Math.Abs(_sy - 1) > 0.001) dc.Pop();
        if (Math.Abs(_tilt) > 0.001) dc.Pop();
        dc.Pop();

        if (_badge is { } badge && _badgeS > 0.01)
            DrawBadge(dc, badge, r, cx, cy);

        // Update hint dot when idle or sleeping
        if (_updateHintColor is { } hintCol && (State == MochiState.Idle || State == MochiState.Sleeping))
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
        const int n = 64;
        double expN = Lerp(2.0 / 2.7, 0.24, mailboxLevel);
        double mx = rx * Lerp(1.0, 1.08, mailboxLevel);
        double my = ry * Lerp(1.0, 0.94, mailboxLevel);
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        for (int i = 0; i <= n; i++)
        {
            double a = (double)i / n * Math.PI * 2;
            double ca = Math.Cos(a);
            double sa = Math.Sin(a);
            double px = mx * (ca >= 0 ? Math.Pow(ca, expN) : -Math.Pow(-ca, expN));
            double py = my * (sa >= 0 ? Math.Pow(sa, expN) : -Math.Pow(-sa, expN));
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
        dc.DrawGeometry(baseGrad, null, body);

        if (_tint > 0.01)
        {
            var tintTop = Color.FromArgb(0, _col.R, _col.G, _col.B);
            var tintBot = Color.FromArgb((byte)Math.Round(255 * 0.72 * _tint), _col.R, _col.G, _col.B);
            var tintGrad = new LinearGradientBrush(tintBot, tintTop, new Point(0.5, 1), new Point(0.5, 0));
            dc.DrawGeometry(tintGrad, null, body);
        }

        // Subtle vignette shadow
        var vig = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.65,
            RadiusY = 0.65
        };
        vig.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
        vig.GradientStops.Add(new GradientStop(Color.FromArgb(48, 0, 0, 0), 1.0));
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
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(128 * blushVal, 0, 255), 255, 120, 150));
        double yOffset = Math.Sin(_yaw) * rx * 0.8;
        foreach (int sd in new[] { -1, 1 })
            dc.DrawEllipse(brush, null, new Point(sd * rx * 0.55 + yOffset, ry * 0.2), r * 0.17, r * 0.10);
        dc.Pop();
    }

    private void DrawEyes(DrawingContext dc, Geometry body, double r, double rx, double ry)
    {
        var shape = _mailboxLevel > 0.5
            ? MochiEyeShape.Cup
            : _eyeOverride ?? (_isDancing && _dancingLevel > 0.15 && !IsMini && (State == MochiState.Idle || State == MochiState.Finished)
                ? MochiEyeShape.Happy
                : _cfg.Eye);
        dc.PushClip(body);
        var ink = new SolidColorBrush(IsMini ? MiniInkColor : InkColor);
        double eyeMult = IsMini ? 1.85 : 1.0;

        foreach (int sd in new[] { -1, 1 })
        {
            double eyeYaw = sd * EyeSp + _yaw;
            double eyePitch = EyeP + _pitch + _roll;
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

    private void DrawEyeShape(DrawingContext dc, MochiEyeShape shape, double w, double h, int sd, Brush ink)
    {
        double t = NowSec();
        switch (shape)
        {
            case MochiEyeShape.Wide:
                DrawEyeShape(dc, MochiEyeShape.Pill, w * 1.16, h * 1.12, sd, ink);
                break;
            case MochiEyeShape.Pill:
            {
                double hh = Math.Max(h * _open, w * 0.3);
                double rad = Math.Min(w / 2, hh / 2);
                dc.DrawRoundedRectangle(ink, null, new Rect(-w / 2, -hh / 2, w, hh), rad, rad);
                break;
            }
            case MochiEyeShape.Dot:
                dc.DrawEllipse(ink, null, new Point(0, 0), w * 0.45, w * 0.45);
                break;
            case MochiEyeShape.Line:
                dc.PushTransform(new RotateTransform(-sd * 11.5));
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.78, -w * 0.21, w * 1.56, w * 0.42), w * 0.21, w * 0.21);
                dc.Pop();
                break;
            case MochiEyeShape.Flat:
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.72, -w * 0.2, w * 1.44, w * 0.4), w * 0.2, w * 0.2);
                break;
            case MochiEyeShape.Happy:
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
            case MochiEyeShape.Closed:
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
            case MochiEyeShape.Spiral:
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
            case MochiEyeShape.Heart:
                DrawHeart(dc, new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6D)), w * 1.15);
                break;
            case MochiEyeShape.Star:
                dc.PushTransform(new RotateTransform(t * 85 * sd));
                DrawStar(dc, new SolidColorBrush(Color.FromRgb(0xF7, 0xB3, 0x2B)), w * 1.05, w * 0.46);
                dc.Pop();
                break;
            case MochiEyeShape.Tired:
                dc.DrawRoundedRectangle(ink, null, new Rect(-w / 2, -h * 0.02, w, h * 0.38), w / 2, w / 2);
                dc.DrawRoundedRectangle(ink, null, new Rect(-w * 0.62, -h * 0.1, w * 1.24, w * 0.22), w * 0.11, w * 0.11);
                break;
            case MochiEyeShape.Wink:
                if (sd < 0) DrawEyeShape(dc, MochiEyeShape.Pill, w, h, sd, ink);
                else DrawEyeShape(dc, MochiEyeShape.Happy, w, h, sd, ink);
                break;
            case MochiEyeShape.Cup:
                DrawEyeShape(dc, MochiEyeShape.Pill, w, h, sd, ink);
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

    private void DrawBadge(DrawingContext dc, MochiBadge badge, double r, double cx, double cy)
    {
        double bs = _badgeS * (IsMini ? 1.25 : 1.0);
        double bx = cx - r * 0.72 * _sx;
        double by = cy - r * 0.72 * _sy;
        double t = NowSec();

        dc.PushTransform(new TranslateTransform(bx, by));
        dc.PushTransform(new ScaleTransform(bs, bs));

        var fill = new SolidColorBrush(badge.Color);
        var black = Brushes.Black;

        if (badge.Kind == MochiBadgeKind.Dots)
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
        else if (badge.Kind is MochiBadgeKind.Bang or MochiBadgeKind.Question)
        {
            dc.DrawEllipse(black, null, new Point(0, 0), r * 0.3, r * 0.3);
            dc.DrawEllipse(fill, null, new Point(0, 0), r * 0.23, r * 0.23);
            if (!IsMini)
            {
                var ft = new FormattedText(
                    badge.Kind == MochiBadgeKind.Bang ? "!" : "?",
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

    // ── Tween Engine ──────────────────────────────────────────────────────────

    private void SetBadge(MochiBadge? b)
    {
        string key = b is { } bd ? $"{bd.Kind}-{bd.Color}" : "none";
        if (key == _badgeKey) return;
        _badgeKey = key;
        int tok = ++_badgeToken;
        Anim(MochiPropKey.BadgeS, [new(0, 90, MochiEase.InOut)]);
        _ = Task.Delay(100).ContinueWith(_ =>
        {
            if (tok != _badgeToken) return;
            _badge = b;
            if (b is not null) Anim(MochiPropKey.BadgeS, [new(1, 280, MochiEase.Back)]);
        });
    }

    private void Anim(MochiPropKey prop, MochiTweenKey[] keys, Action? onComplete = null)
    {
        _tweens[prop] = new MochiTween
        {
            Prop = prop,
            Keys = keys,
            Index = 0,
            From = GetProp(prop),
            StartMs = NowMs(),
            OnComplete = onComplete
        };
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

    private double GetProp(MochiPropKey p) => p switch
    {
        MochiPropKey.Yaw => _yaw, MochiPropKey.Pitch => _pitch, MochiPropKey.Roll => _roll,
        MochiPropKey.Tilt => _tilt, MochiPropKey.Open => _open, MochiPropKey.Sx => _sx,
        MochiPropKey.Sy => _sy, MochiPropKey.Oy => _oy, MochiPropKey.Ox => _ox,
        MochiPropKey.Tint => _tint, MochiPropKey.Hands => _hands, MochiPropKey.Blush => _blush,
        MochiPropKey.Es => _es, MochiPropKey.BadgeS => _badgeS,
        _ => 0
    };

    private void SetProp(MochiPropKey p, double v)
    {
        switch (p)
        {
            case MochiPropKey.Yaw: _yaw = v; break;
            case MochiPropKey.Pitch: _pitch = v; break;
            case MochiPropKey.Roll: _roll = v; break;
            case MochiPropKey.Tilt: _tilt = v; break;
            case MochiPropKey.Open: _open = v; break;
            case MochiPropKey.Sx: _sx = v; break;
            case MochiPropKey.Sy: _sy = v; break;
            case MochiPropKey.Oy: _oy = v; break;
            case MochiPropKey.Ox: _ox = v; break;
            case MochiPropKey.Tint: _tint = v; break;
            case MochiPropKey.Hands: _hands = v; break;
            case MochiPropKey.Blush: _blush = v; break;
            case MochiPropKey.Es: _es = v; break;
            case MochiPropKey.BadgeS: _badgeS = v; break;
        }
    }

    private static double ApplyEase(MochiEase ease, double t) => ease switch
    {
        MochiEase.Linear => t,
        MochiEase.Out => 1 - Math.Pow(1 - t, 3),
        MochiEase.InOut => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
        MochiEase.Back => 1 + 2.70158 * Math.Pow(t - 1, 3) + 1.70158 * Math.Pow(t - 1, 2),
        _ => t
    };

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Color MixColor(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(Lerp(a.R, b.R, t)),
        (byte)Math.Round(Lerp(a.G, b.G, t)),
        (byte)Math.Round(Lerp(a.B, b.B, t)));
}
