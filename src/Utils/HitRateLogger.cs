using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;

namespace InsanityGaming.RngFix.Utils;

/// <summary>
/// Aggregates trace DidHit events over a configurable tick window and emits a single
/// LogInformation summary when the window expires. Tracks the most-frequent collision
/// attribute combination so the dominant cause is always visible in the log.
/// </summary>
internal sealed class HitRateLogger
{
    private const int TickInterval = 32;

    private readonly string _site;
    private readonly ISharedSystem _sharedSystem;
    private readonly ILogger _logger;
    private readonly float _normalHitsPerTickPerPlayer;
    private int _periodStartTick = -1;
    private int _totalHits;
    private (InteractionLayers A, InteractionLayers W, CollisionGroupType G, int Count) _topHit;
    private Dictionary<(InteractionLayers, InteractionLayers, CollisionGroupType), int>? _counts;
    private HashSet<int>? _players;

    /// <param name="normalHitsPerTickPerPlayer">
    /// Expected hits-per-tick per active player under normal conditions.
    /// Windows are only logged when the observed rate exceeds this baseline. Pass 0 to always log.
    /// </param>
    public HitRateLogger(string site, ISharedSystem sharedSystem, ILogger logger, float normalHitsPerTickPerPlayer = 0f)
    {
        _site                       = site;
        _sharedSystem               = sharedSystem;
        _logger                     = logger;
        _normalHitsPerTickPerPlayer = normalHitsPerTickPerPlayer;
    }

    public void Record(int playerEntityIndex, InteractionLayers interactsAs, InteractionLayers interactsWith, CollisionGroupType collisionGroup)
    {
        int curTick = _sharedSystem.GetModSharp().GetGlobals().TickCount;

        if (_periodStartTick < 0)
            _periodStartTick = curTick;

        if (curTick > _periodStartTick + TickInterval)
        {
            Flush(curTick - _periodStartTick);
            _periodStartTick = curTick;
        }

        _totalHits++;
        _players ??= new HashSet<int>();
        _players.Add(playerEntityIndex);

        _counts ??= new Dictionary<(InteractionLayers, InteractionLayers, CollisionGroupType), int>();
        var key = (interactsAs, interactsWith, collisionGroup);
        _counts.TryGetValue(key, out int c);
        c++;
        _counts[key] = c;
        if (c > _topHit.Count)
            _topHit = (interactsAs, interactsWith, collisionGroup, c);
    }

    private void Flush(int elapsedTicks)
    {
        if (_totalHits == 0) return;

        int playerCount = _players?.Count ?? 1;
        float hitsPerPlayer = (float)_totalHits / playerCount;

        if (_normalHitsPerTickPerPlayer <= 0f || hitsPerPlayer / elapsedTicks > _normalHitsPerTickPerPlayer)
            _logger.LogInformation(
                "[{Site}] {Total} hits / {Players} players = {PerPlayer:F1} hits/player over {Ticks} ticks | top: InteractsAs={A} InteractsWith={W} Group={G} ({Count}x)",
                _site, _totalHits, playerCount, hitsPerPlayer, elapsedTicks, _topHit.A, _topHit.W, _topHit.G, _topHit.Count);

        _totalHits = 0;
        _players?.Clear();
        _counts?.Clear();
        _topHit = default;
    }
}
