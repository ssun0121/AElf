using AElf.Types;

namespace AElf.WebApp.Application.Chain.Dto;

public class VirtualTransactionDto
{
    public long BlockNumber { get; set; }
    public Hash BlockHash { get; set; }
    public Address ParentContract { get; set; }
    public Hash VirtualHash { get; set; }
    public Address To { get; set; }
    public string MethodName { get; set; }
    public string Params { get; set; }
    public Address Signatory { get; set; }
    public Hash ParentVirtualTransactionId { get;set; }
}