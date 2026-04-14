using GlDrive.Ftp;

namespace GlDrive.Spread;

public enum FxpMode { PasvPasv, CpsvPasv, PasvCpsv, Relay }

public static class FxpModeDetector
{
    public static FxpMode Detect(FtpConnectionPool source, FtpConnectionPool dest)
    {
        return (source.UseCpsv, dest.UseCpsv) switch
        {
            (false, false) => FxpMode.PasvPasv,
            (true, false) => FxpMode.CpsvPasv,
            (false, true) => FxpMode.PasvCpsv,
            (true, true) => FxpMode.Relay,
        };
    }
}
