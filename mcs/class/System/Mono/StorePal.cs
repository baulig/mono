using System;

namespace Internal.Cryptography.Pal
{
    internal sealed partial class StorePal
    {
        public static IExportPal FromCertificate(ICertificatePalCore cert)
        {
                throw new PlatformNotSupportedException();
        }
    }
}
