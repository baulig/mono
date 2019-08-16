namespace System.Net {

	partial class SocketAddress
	{
		internal byte[] Buffer => m_Buffer;

		internal SocketAddress (byte[] buffer, int size)
		{
			InternalSize = size;
			m_Buffer = new byte[(size/IntPtr.Size+2)*IntPtr.Size];//sizeof DWORD

			global::System.Buffer.BlockCopy (buffer, 0, m_Buffer, 0, buffer.Length);
		}
	}
}
