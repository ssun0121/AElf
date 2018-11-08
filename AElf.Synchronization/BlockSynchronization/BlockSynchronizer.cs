using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.ChainController.EventMessages;
using AElf.Common;
using AElf.Configuration.Config.Chain;
using AElf.Kernel;
using AElf.Kernel.EventMessages;
using AElf.Miner.EventMessages;
using AElf.Synchronization.BlockExecution;
using AElf.Synchronization.EventMessages;
using Easy.MessageHub;
using NLog;

// ReSharper disable once CheckNamespace
namespace AElf.Synchronization.BlockSynchronization
{
    public class BlockSynchronizer : IBlockSynchronizer
    {
        private readonly IChainService _chainService;
        private readonly IBlockValidationService _blockValidationService;
        private readonly IBlockExecutor _blockExecutor;

        private readonly IBlockSet _blockSet;

        private IBlockChain _blockChain;

        private readonly ILogger _logger;

        private IBlockChain BlockChain => _blockChain ?? (_blockChain =
                                              _chainService.GetBlockChain(
                                                  Hash.LoadHex(ChainConfig.Instance.ChainId)));

        private const ulong BlockCacheLimit = 64;

        public static ulong ForkDetectionLength = 4;

        private bool _minedBlock;

        private static int _flag;

        private static ulong _firstFutureBlockHeight;

        private static bool _miningStarted;

        private static bool _executingRemainingBlocks;

        private static IBlock _nextBlock;

        private static ulong _heightBeforeRollback;

        private static ulong _heightOfUnlinkableBlock;

        private static ulong _latestHandledValidBlock;

        private static bool _isExecuting;

        public BlockSynchronizer(IChainService chainService, IBlockValidationService blockValidationService,
            IBlockExecutor blockExecutor, IBlockSet blockSet)
        {
            _chainService = chainService;
            _blockValidationService = blockValidationService;
            _blockExecutor = blockExecutor;
            _blockSet = blockSet;

            _logger = LogManager.GetLogger(nameof(BlockSynchronizer));

            MessageHub.Instance.Subscribe<HeadersReceived>(async inHeaders =>
            {
                var headers = inHeaders.Headers.OrderByDescending(h => h.Index).ToList();
                if (!headers.Any())
                    return;
                foreach (var blockHeader in headers)
                {
                    var correspondingBlockHeader = await BlockChain.GetBlockByHeightAsync(blockHeader.Index - 1);
                    if (correspondingBlockHeader.BlockHashToHex != blockHeader.PreviousBlockHash.DumpHex())
                        continue;
                    MessageHub.Instance.Publish(new HeaderAccepted(blockHeader));
                    return;
                }

                MessageHub.Instance.Publish(new UnlinkableHeader(headers.Last()));
            });

            MessageHub.Instance.Subscribe<DPoSStateChanged>(inState => { _miningStarted = inState.IsMining; });

            MessageHub.Instance.Subscribe<BlockMined>(inBlock => { AddMinedBlock(inBlock.Block); });
            
            MessageHub.Instance.Subscribe<ExecutionStateChanged>(inState => { _isExecuting = inState.IsExecuting; });
        }

        public async Task<BlockExecutionResult> ReceiveBlock(IBlock block)
        {
            if (_blockSet.IsBlockReceived(block.GetHash(), block.Index))
            {
                return BlockExecutionResult.AlreadyReceived;
            }

            var currentBlockHeight = await BlockChain.GetCurrentBlockHeightAsync();

            _latestHandledValidBlock = Math.Max(_latestHandledValidBlock, currentBlockHeight);

            if (block.Index > _latestHandledValidBlock + 1)
            {
                if (_firstFutureBlockHeight == 0)
                    _firstFutureBlockHeight = block.Index;

                _blockSet.AddBlock(block);

                _logger?.Trace($"Added block {block.BlockHashToHex} to block cache cause this is a future block.");

                if (block.Index >= currentBlockHeight + ForkDetectionLength)
                {
                    _heightOfUnlinkableBlock = block.Index;
                    await ReviewBlockSet();
                }

                return BlockExecutionResult.FutureBlock;
            }

            if (block.Index == _latestHandledValidBlock + 1)
            {
                _nextBlock = block;
            }

            return await HandleBlock(block);
        }

        private async Task<BlockExecutionResult> HandleBlock(IBlock block)
        {
            _logger?.Trace("Trying to enter HandleBlock");
            var lockWasTaken = Interlocked.CompareExchange(ref _flag, 1, 0) == 0;
            if (lockWasTaken)
            {
                _logger?.Trace("Entered HandleBlock");

                var blockValidationResult =
                    await _blockValidationService.ValidateBlockAsync(block, await GetChainContextAsync());

                if (blockValidationResult.IsSuccess())
                {
                    return await HandleValidBlock(block);
                }

                await HandleInvalidBlock(block, blockValidationResult);
            }

            return BlockExecutionResult.NotExecuted;
        }

        public async Task ExecuteRemainingBlocks(ulong targetHeight)
        {
            _executingRemainingBlocks = true;
            // Find new blocks from block set to execute
            var blocks = _blockSet.GetBlockByHeight(targetHeight);
            ulong i = 0;
            var currentBlockHash = await BlockChain.GetCurrentBlockHashAsync();
            while (blocks != null && blocks.Any(b => b.Header.PreviousBlockHash.DumpHex() == currentBlockHash.DumpHex()))
            {
                _logger?.Trace($"Will get block of height {targetHeight + i} from block set to " +
                               $"execute - {blocks.Count} blocks.");

                i++;
                foreach (var block in blocks)
                {
                    blocks = _blockSet.GetBlockByHeight(targetHeight + i);
                    var executionResult = await HandleValidBlock(block);
                    if (executionResult.IsFailed())
                    {
                        _executingRemainingBlocks = false;
                        return;
                    }
                }
            }

            _executingRemainingBlocks = false;
        }

        public void AddMinedBlock(IBlock block)
        {
            _blockSet.Tell(block);

            _minedBlock = true;

            // Update DPoS process.
            // TODO this can probably removed by subscribing to BlockMined in DPoS.
            MessageHub.Instance.Publish(UpdateConsensus.Update);

            // We can say the "initial sync" is finished, set KeepHeight to a specific number
            if (_blockSet.KeepHeight == ulong.MaxValue)
            {
                _logger?.Trace($"Set the limit of the branched blocks cache in block set to {BlockCacheLimit}.");
                _blockSet.KeepHeight = BlockCacheLimit;
            }
        }

        private async Task<BlockExecutionResult> HandleValidBlock(IBlock block)
        {
            _latestHandledValidBlock = block.Index;
            
            _logger?.Trace($"Valid block {block.BlockHashToHex}. Height: **{block.Index}**");

            _blockSet.AddBlock(block);

            var executionResult = await _blockExecutor.ExecuteBlock(block);

            _logger?.Trace($"Block execution result: {executionResult}.");

            if (executionResult.NeedToRollback())
            {
                // Need to rollback one block:
                await BlockChain.RollbackOneBlock();
                _blockSet.InformRollback(block.Index, block.Index);

                // Basically re-sync the block of specific height.
                await ExecuteRemainingBlocks(block.Index);

                return executionResult;
            }

            if (executionResult.CannotExecute())
            {
                _logger?.Trace($"Cannot execute block {block.BlockHashToHex} of height {block.Index}");
                return executionResult;
            }

            if (executionResult.CanExecuteAgain())
            {
                // No need to rollback:
                // Receive again to execute the same block.

                var currentBlockHash = await BlockChain.GetCurrentBlockHashAsync();
                if (_minedBlock && !_executingRemainingBlocks)
                {
                    Thread.Sleep(200);
                    MessageHub.Instance.Publish(new LockMining(false));
                    BlockExecutionResult reExecutionResult1;
                    do
                    {
                        var reValidationResult = await _blockValidationService.ExecutingAgain(true)
                            .ValidateBlockAsync(block, await GetChainContextAsync());
                        
                        _logger?.Trace($"Block execution result: {reValidationResult}.");

                        if (reValidationResult.IsFailed())
                        {
                            break;
                        }

                        reExecutionResult1 = await _blockExecutor.ExecuteBlock(block);
                        if (_blockSet.MultipleLinkableBlocksInOneIndex(block.Index, currentBlockHash.DumpHex()))
                        {
                            Thread.VolatileWrite(ref _flag, 0);
                            return reExecutionResult1;
                        }
                    } while (reExecutionResult1.CanExecuteAgain() && !_miningStarted);
                    
                    Thread.VolatileWrite(ref _flag, 0);
                    return executionResult;
                }

                BlockExecutionResult reExecutionResult2;
                do
                {
                    Thread.Sleep(100);
                    var reValidationResult = await _blockValidationService.ExecutingAgain(true)
                        .ValidateBlockAsync(block, await GetChainContextAsync());

                    _logger?.Trace($"Block execution result: {reValidationResult}.");

                    if (reValidationResult.IsFailed())
                    {
                        break;
                    }

                    reExecutionResult2 = await _blockExecutor.ExecuteBlock(block);
                    if (_blockSet.MultipleLinkableBlocksInOneIndex(block.Index, currentBlockHash.DumpHex()))
                    {
                        Thread.VolatileWrite(ref _flag, 0);
                        return reExecutionResult2;
                    }
                } while (reExecutionResult2.CanExecuteAgain());
            }

            _blockSet.Tell(block);

            // Update the consensus information.
            MessageHub.Instance.Publish(UpdateConsensus.Update);

            Thread.VolatileWrite(ref _flag, 0);

            // Notify the network layer the block has been executed.
            MessageHub.Instance.Publish(new BlockExecuted(block));

            // In case of this synchronization run so long of time.
            if (_nextBlock?.Header != null && _nextBlock.Index == block.Index + 1)
            {
                await ReceiveBlock(_nextBlock);
            }

            // Sync future blocks.
            if (block.Index + 1 == _firstFutureBlockHeight)
            {
                await ExecuteRemainingBlocks(_firstFutureBlockHeight);
            }

            if (_heightBeforeRollback != 0)
            {
                if (block.Index >= _heightBeforeRollback)
                {
                    _heightBeforeRollback = 0;
                    _heightOfUnlinkableBlock = 0;
                    MessageHub.Instance.Publish(new CatchingUpAfterRollback(false));
                }
                else
                {
                    MessageHub.Instance.Publish(new CatchingUpAfterRollback(true));
                }
            }

            return BlockExecutionResult.Success;
        }

        private async Task HandleInvalidBlock(IBlock block, BlockValidationResult blockValidationResult)
        {
            Thread.VolatileWrite(ref _flag, 0);

            _logger?.Warn($"Invalid block {block.BlockHashToHex} : {blockValidationResult.ToString()}. Height: **{block.Index}**");

            MessageHub.Instance.Publish(new LockMining(false));

            // Handle the invalid blocks according to their validation results.
            if ((int) blockValidationResult < 100)
            {
                _blockSet.AddBlock(block);
            }

            if (blockValidationResult == BlockValidationResult.Unlinkable)
            {
                _heightOfUnlinkableBlock = block.Index;

                _logger?.Warn("Received unlinkable block.");

                MessageHub.Instance.Publish(new UnlinkableHeader(block.Header));
            }

            // Received blocks from branched chain.
            if (blockValidationResult == BlockValidationResult.BranchedBlock)
            {
                _logger?.Warn("Received a block from branched chain.");

                await ReviewBlockSet();
            }

            // A weird situation.
            if (blockValidationResult == BlockValidationResult.Pending)
            {
                _heightOfUnlinkableBlock = block.Index;
                
                await ReviewBlockSet();
            }
        }

        private async Task ReviewBlockSet()
        {
            if (_heightOfUnlinkableBlock == 0 || _isExecuting)
            {
                return;
            }

            // In case of the block set exists blocks that should be valid but didn't executed yet.
            var currentHeight = await BlockChain.GetCurrentBlockHeightAsync();

            if (BlockSet.MaxHeight.HasValue && BlockSet.MaxHeight < currentHeight + ForkDetectionLength)
            {
                return;
            }
            
            // Detect longest chain and switch.
            var forkHeight = _blockSet.AnyLongerValidChain(currentHeight - ForkDetectionLength);
            // Execute next block.
            if (forkHeight == ulong.MaxValue)
            {
                await ExecuteRemainingBlocks(currentHeight + 1);
            }
            else if (forkHeight != 0)
            {
                await RollbackToHeight(forkHeight, currentHeight - ForkDetectionLength);
            }
        }

        private async Task RollbackToHeight(ulong targetHeight, ulong currentHeight)
        {
            _heightBeforeRollback = currentHeight;
            await BlockChain.RollbackToHeight(targetHeight - 1);
            _blockSet.InformRollback(targetHeight, currentHeight);
            await ExecuteRemainingBlocks(targetHeight);
        }

        private async Task<IChainContext> GetChainContextAsync()
        {
            var chainId = Hash.LoadHex(ChainConfig.Instance.ChainId);
            var blockchain = _chainService.GetBlockChain(chainId);
            IChainContext chainContext = new ChainContext
            {
                ChainId = chainId,
                BlockHash = await blockchain.GetCurrentBlockHashAsync()
            };

            if (chainContext.BlockHash != Hash.Genesis && chainContext.BlockHash != null)
            {
                chainContext.BlockHeight =
                    ((BlockHeader) await blockchain.GetHeaderByHashAsync(chainContext.BlockHash)).Index;
            }

            return chainContext;
        }

        public IBlock GetBlockByHash(Hash blockHash)
        {
            return _blockSet.GetBlockByHash(blockHash) ?? BlockChain.GetBlockByHashAsync(blockHash).Result;
        }

        public async Task<BlockHeaderList> GetBlockHeaderList(ulong index, int count)
        {
            var blockHeaderList = new BlockHeaderList();
            for (var i = index; i > index - (ulong) count; i--)
            {
                var block = await BlockChain.GetBlockByHeightAsync(i);
                blockHeaderList.Headers.Add(block.Header);
            }

            return blockHeaderList;
        }
    }
}