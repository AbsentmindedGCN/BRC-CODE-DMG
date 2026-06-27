namespace BRCCodeDmg
{
    internal static class PopupState
    {
        private static float _suppressUntil = -1f;
        private static float _chatInputSuppressUntil = -1f;

        public static bool AnyVisible =>
            MasterMenu.IsVisible             ||
            RomSelectMenu.IsVisible          ||
            LinkCablePlayerList.IsVisible    ||
            LinkCableConnectDialog.IsVisible ||
            VolumeMenu.IsVisible             ||
            GBPaletteMenu.IsVisible;

        public static void SuppressPhoneNavFor(float seconds = 0.2f)
        {
            _suppressUntil = UnityEngine.Time.unscaledTime + seconds;
        }

        public static bool IsSuppressed =>
            UnityEngine.Time.unscaledTime <= _suppressUntil;

        public static bool ShouldSuppressMenuInputForChat()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (AppCodeDmg.IsChatInputActive())
                _chatInputSuppressUntil = now + 0.35f;

            return now <= _chatInputSuppressUntil;
        }
    }
}
