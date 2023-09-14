using System.Linq;
using AElf.Kernel.Blockchain.Infrastructure;
using AElf.Kernel.Infrastructure;

namespace AElf.Kernel.Blockchain.Domain;

public interface IVirtualTransactionManager
{
    Task AddVirtualTransactionsAsync(Hash txId, BlockHeader blockHeader,
        IList<VirtualTransaction> virtualTransactionList);

    Task<List<VirtualTransaction>> GetVirtualTransactionsAsync(Hash txId, Hash disambiguationHash,
        IList<Hash> virtualTransactionIds);

    Task<VirtualTransactionSet> GetVirtualTransactionSetAsync(Hash txId, Hash disambiguationHash);
}

public class VirtualTransactionManager : IVirtualTransactionManager
{
    private readonly IBlockchainStore<VirtualTransactionSet> _virtualTransactionSet;
    private readonly IBlockchainStore<VirtualTransaction> _virtualTransaction;

    public VirtualTransactionManager(IBlockchainStore<VirtualTransactionSet> virtualTransactionSet,
        IBlockchainStore<VirtualTransaction> virtualTransaction)
    {
        _virtualTransactionSet = virtualTransactionSet;
        _virtualTransaction = virtualTransaction;
    }

    public async Task AddVirtualTransactionsAsync(Hash txId, BlockHeader blockHeader,
        IList<VirtualTransaction> virtualTransactionList)
    {
        var id = HashHelper.XorAndCompute(txId, blockHeader.GetDisambiguatingHash());
        var virtualTxIds = await AddVirtualTransactionAsync(id, virtualTransactionList);
        await _virtualTransactionSet.SetAsync(id.ToStorageKey(), new VirtualTransactionSet
        {
            BlockHash = blockHeader.GetHash(),
            BlockNumber = blockHeader.Height,
            TransactionId = txId,
            VirtualTransactionIds = { virtualTxIds }
        });
    }

    private async Task<IList<Hash>> AddVirtualTransactionAsync(Hash id,
        IList<VirtualTransaction> virtualTransactionList)
    {
        var virtualTxIdList = new List<Hash>();
        await _virtualTransaction.SetAllAsync(virtualTransactionList.ToDictionary(v =>
            {
                var virtualTxId = v.GetHash();
                virtualTxIdList.Add(virtualTxId);
                return GetVirtualTxStorageKey(id, virtualTxId);
            },
            v =>
            {
                v.Index = virtualTransactionList.IndexOf(v);
                return v;
            }));
        return virtualTxIdList;
    }

    public async Task<List<VirtualTransaction>> GetVirtualTransactionsAsync(Hash txId, Hash disambiguationHash,
        IList<Hash> virtualTransactionIds)
    {
        var id = HashHelper.XorAndCompute(txId, disambiguationHash);
        return await _virtualTransaction.GetAllAsync(virtualTransactionIds.Select(v => GetVirtualTxStorageKey(id, v))
            .ToList());
    }

    public async Task<VirtualTransactionSet> GetVirtualTransactionSetAsync(Hash txId, Hash disambiguationHash)
    {
        var id = HashHelper.XorAndCompute(txId, disambiguationHash);
        return await _virtualTransactionSet.GetAsync(id.ToStorageKey());
    }

    private static string GetVirtualTxStorageKey(Hash id, Hash virtualTxId)
    {
        return HashHelper.XorAndCompute(id, virtualTxId).ToStorageKey();
    }
}