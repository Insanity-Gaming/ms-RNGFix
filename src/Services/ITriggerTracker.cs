namespace InsanityGaming.RngFix.Services;

public interface ITriggerTracker
{
    void SetTouching(int playerEntityIndex, int triggerEntityIndex, bool touching);
    bool IsTouching(int playerEntityIndex, int triggerEntityIndex);
}
