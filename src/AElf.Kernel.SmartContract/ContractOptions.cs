namespace AElf.Kernel.SmartContract;

public class ContractOptions
{
    public bool ContractDeploymentAuthorityRequired { get; set; } = true;
    public string GenesisContractDir { get; set; }
    public bool VirtualLogEventsRequired { get; set; } = true;
    
}