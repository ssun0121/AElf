using Google.Protobuf;

namespace AElf.Types;

public partial class VirtualTransaction
{
    private Hash _virtualTransactionId;

    public Hash GetHash()
    {
        if (_virtualTransactionId != null) 
            return _virtualTransactionId;
        
        var virtualTransaction = Clone();
        _virtualTransactionId = HashHelper.ComputeFrom(virtualTransaction.ToByteArray());
        
        return _virtualTransactionId;
    }
}