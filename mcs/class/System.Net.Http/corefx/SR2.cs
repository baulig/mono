partial class SR
{
        internal static string Format (string resourceFormat, params object[] args)
        {
		if (args != null)
			return string.Format (resourceFormat, args);

		return resourceFormat;
	}
}
