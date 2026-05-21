namespace PropFirmGuardian.Models
{
    public enum AccountState
    {
        Active,
        Warning,
        GraceWindow,
        Flattening,
        Locked,
        RecoveryMode,
        NewsLocked,
        Disconnected,
        HardLocked
    }
}
