namespace CardShopCoop.Sync
{
    internal static class RegisterVisitIdentity
    {
        internal static bool Matches(int expectedId, int expectedGeneration, int actualId, int actualGeneration)
            => expectedId != 0 && expectedId == actualId && expectedGeneration == actualGeneration;
    }
}
