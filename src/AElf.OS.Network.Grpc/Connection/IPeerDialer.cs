using System.Net;
using System.Threading.Tasks;

namespace AElf.OS.Network.Grpc
{
    public interface IPeerDialer
    {
        Task<GrpcPeer> DialPeerAsync(IPEndPoint remoteEndPoint);
        Task<GrpcPeer> DialBackPeerAsync(IPEndPoint endpoint, Handshake handshake);
    }
}