using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private long _tutorialSequence;
        private readonly TutorialContributionLedger _tutorialContributions = new TutorialContributionLedger(
            (int)ETutorialTaskCondition.PutCardOnShelf,
            (int)ETutorialTaskCondition.PickupBox, (int)ETutorialTaskCondition.PutItemOnShelf,
            (int)ETutorialTaskCondition.SetItemPrice, (int)ETutorialTaskCondition.OpenPack,
            (int)ETutorialTaskCondition.PutCardOnShelf, (int)ETutorialTaskCondition.InteractCashierCounter);

        internal void ObserveTutorialAction(ETutorialTaskCondition condition, float delta)
        {
            if (Role == CoopRole.Host)
            {
                _shopState.ForceResend();
                return;
            }
            if (Role != CoopRole.Client || _net == null || ClientReloading || !InGameLevel()
                || _joinWatchdog.Current != Net.JoinWatchdog.Stage.Idle
                || _world.ApplyingRemote || BoxShared.ApplyingRemote || ShopStateSync.ApplyingRemote
                || Patches.GamePatches.ApplyingRemotePrice || !_tutorialContributions.Valid((int)condition, delta))
                return;
            Send(1, new TutorialContributionMessage
            {
                Sequence = ++_tutorialSequence,
                Condition = (int)condition,
                Delta = delta
            });
        }

        private void ApplyTutorialContribution(int connection, TutorialContributionMessage message)
        {
            if (Role != CoopRole.Host || !InGameLevel() || !PeerNames.ContainsKey(connection)
                || !_tutorialContributions.Accept(connection, message.Sequence, message.Condition, message.Delta))
                return;
            // Reserve the sequence before vanilla execution: a dispatcher retry must
            // not add progress twice, including after a partial UI failure.
            TutorialManager.AddTaskValue((ETutorialTaskCondition)message.Condition, message.Delta);
            _shopState.ForceResend();
        }
    }
}
