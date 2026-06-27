namespace BRCCodeDmg.Transport.Packets
{
    internal static class LinkCablePacketIds
    {
        public const string Invite          = "codedmg.linkcable_invite";
        public const string Accept          = "codedmg.linkcable_accept";
        public const string Decline         = "codedmg.linkcable_decline";
        public const string Disconnect      = "codedmg.linkcable_disconnect";
        public const string SerialByte      = "codedmg.linkcable_serial";
        public const string RomHash         = "codedmg.linkcable_romhash";
        public const string SaveState       = "codedmg.linkcable_savestate";
        public const string LinkSettings    = "codedmg.linkcable_settings";
        public const string Joypad          = "codedmg.linkcable_joypad";
    }
}
