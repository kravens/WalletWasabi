using NBitcoin;
using System.Linq;
using WalletWasabi.Blockchain.Keys;

namespace WalletWasabi.Hwi.Trezor;

public static class TrezorExtensions
{
	public static KeyPath? TryGetKeyPath(this KeyManager keyManager, Script scriptPubKey) =>
		keyManager.GetKeys(key => key.ContainsScript(scriptPubKey)).FirstOrDefault()?.FullKeyPath;
}
