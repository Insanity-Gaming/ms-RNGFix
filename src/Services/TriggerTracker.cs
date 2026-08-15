namespace InsanityGaming.RngFix.Services;

public sealed class TriggerTracker : ITriggerTracker
{
    private readonly IPlayerStateService _playerStateService;

    public TriggerTracker(IPlayerStateService playerStateService)
    {
        _playerStateService = playerStateService;
    }

    public void SetTouching(int playerEntityIndex, int triggerEntityIndex, bool touching)
    {
        var triggers = _playerStateService.Get(playerEntityIndex)?.TouchingTriggers;
        if (triggers is null)
            return;

        if (touching)
        {
            if (!triggers.Contains(triggerEntityIndex))
                triggers.Add(triggerEntityIndex);
        }
        else
        {
            triggers.Remove(triggerEntityIndex);
        }
    }

    public bool IsTouching(int playerEntityIndex, int triggerEntityIndex)
    {
        var triggers = _playerStateService.Get(playerEntityIndex)?.TouchingTriggers;
        return triggers?.Contains(triggerEntityIndex) ?? false;
    }
}
