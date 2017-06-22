#define MARTIN_DEBUG
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;

namespace System.Net
{
	class WebRequestStream : WebConnectionStream
	{
		public WebRequestStream (WebConnection connection, WebOperation operation, WebConnectionData data)
			: base (connection, operation, data, operation.Request)
		{
			
		}
	}
}
