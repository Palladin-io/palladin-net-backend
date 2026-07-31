namespace Palladin.Core.Security;

public interface IServerKeyDeriver
{
    byte[] DeriveHmacKey(string purpose);
}
