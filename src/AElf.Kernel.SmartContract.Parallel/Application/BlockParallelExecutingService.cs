using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Miner.Application;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract.Domain;
using AElf.Kernel.SmartContractExecution.Application;

namespace AElf.Kernel.SmartContract.Parallel.Application;

public class BlockParallelExecutingService : BlockExecutingService
{
    public BlockParallelExecutingService(ITransactionExecutingService transactionExecutingService,
        IBlockchainStateService blockchainStateService, ITransactionResultService transactionResultService,
        ISystemTransactionExtraDataProvider systemTransactionExtraDataProvider,
        IVirtualTransactionService virtualTransactionService) : base(
        transactionExecutingService, blockchainStateService, transactionResultService,
        systemTransactionExtraDataProvider, virtualTransactionService)
    {
    }

    protected override async Task CleanUpReturnSetCollectionAsync(BlockHeader blockHeader,
        ExecutionReturnSetCollection executionReturnSetCollection)
    {
        await base.CleanUpReturnSetCollectionAsync(blockHeader, executionReturnSetCollection);
        if (executionReturnSetCollection.Conflict.Count > 0)
            await EventBus.PublishAsync(new ConflictingTransactionsFoundInParallelGroupsEvent(
                blockHeader, executionReturnSetCollection.Executed.ToList(),
                executionReturnSetCollection.Conflict
            ));
    }
}