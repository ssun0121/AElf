using AElf.Kernel.Blockchain.Domain;

namespace AElf.Kernel.Blockchain.Application;

public interface IVirtualTransactionService
{
    Task AddVirtualTransactionsAsync(Hash txId, BlockHeader blockHeader,
        List<VirtualTransaction> virtualTransactionList);

    Task<List<VirtualTransaction>> GetVirtualTransactionsAsync(Hash txId, IList<Hash> virtualTransactionIds);
    Task<VirtualTransactionSet> GetVirtualTransactionSetAsync(Hash txId);
}

public class VirtualTransactionService : IVirtualTransactionService
{
    private readonly IVirtualTransactionManager _virtualTransactionManager;
    private readonly ITransactionBlockIndexService _transactionBlockIndexService;


    public VirtualTransactionService(IVirtualTransactionManager virtualTransactionManager,
        ITransactionBlockIndexService transactionBlockIndexService)
    {
        _virtualTransactionManager = virtualTransactionManager;
        _transactionBlockIndexService = transactionBlockIndexService;
    }

    public async Task AddVirtualTransactionsAsync(Hash txId, BlockHeader blockHeader,
        List<VirtualTransaction> virtualTransactionList)
    {
        await _virtualTransactionManager.AddVirtualTransactionsAsync(txId, blockHeader, virtualTransactionList);
    }

    public async Task<List<VirtualTransaction>> GetVirtualTransactionsAsync(Hash txId,
        IList<Hash> virtualTransactionIds)
    {
        var transactionBlockIndex =
            await _transactionBlockIndexService.GetTransactionBlockIndexAsync(txId);

        return await _virtualTransactionManager.GetVirtualTransactionsAsync(txId, transactionBlockIndex.BlockHash,
            virtualTransactionIds);
    }

    public async Task<VirtualTransactionSet> GetVirtualTransactionSetAsync(Hash txId)
    {
        var transactionBlockIndex =
            await _transactionBlockIndexService.GetTransactionBlockIndexAsync(txId);

        return await _virtualTransactionManager.GetVirtualTransactionSetAsync(txId, transactionBlockIndex.BlockHash);
    }
}