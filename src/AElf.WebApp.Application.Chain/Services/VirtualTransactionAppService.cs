using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Types;
using AElf.WebApp.Application.Chain.Dto;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.ObjectMapping;

namespace AElf.WebApp.Application.Chain;

public interface IVirtualTransactionAppService
{
    Task<List<VirtualTransactionDto>> GetVirtualTransactionsAsync(string transactionId);
}

public class VirtualTransactionAppService : IVirtualTransactionAppService
{
    private readonly IObjectMapper<ChainApplicationWebAppAElfModule> _objectMapper;
    private readonly IVirtualTransactionService _virtualTransactionService;
    private readonly WebAppOptions _webAppOptions;


    public VirtualTransactionAppService(IObjectMapper<ChainApplicationWebAppAElfModule> objectMapper,
        IVirtualTransactionService virtualTransactionService, IOptionsMonitor<WebAppOptions> optionsSnapshot)
    {
        _objectMapper = objectMapper;
        _virtualTransactionService = virtualTransactionService;
        _webAppOptions = optionsSnapshot.CurrentValue;
    }

    public async Task<List<VirtualTransactionDto>> GetVirtualTransactionsAsync(string transactionId)
    {
        Hash transactionIdHash;
        try
        {
            transactionIdHash = Hash.LoadFromHex(transactionId);
        }
        catch
        {
            throw new UserFriendlyException(Error.Message[Error.InvalidTransactionId],
                Error.InvalidTransactionId.ToString());
        }
        var output = new List<VirtualTransactionDto>();
        var virtualTransactionSet = await _virtualTransactionService.GetVirtualTransactionSetAsync(transactionIdHash);
        if (virtualTransactionSet == null) return output;
        
        var virtualTransactionList =
            await _virtualTransactionService.GetVirtualTransactionsAsync(transactionIdHash,
                virtualTransactionSet.VirtualTransactionIds);
        output = virtualTransactionList.Select(v =>
            _objectMapper.GetMapper()
                .Map<VirtualTransaction, VirtualTransactionDto>(v,
                    opt => opt.Items[TransactionProfile.ErrorTrace] = _webAppOptions.IsDebugMode)
        ).Select(o =>
        {
            o.BlockHash = virtualTransactionSet.BlockHash;
            o.BlockNumber = virtualTransactionSet.BlockNumber;
            return o;
        }).ToList();

        return output;
    }
}