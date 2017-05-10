// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Net.Http;

namespace System.Net
{
    public partial class WebException : InvalidOperationException
    {
        internal static WebExceptionStatus GetStatusFromException(HttpRequestException ex)
        {
            return WebExceptionStatus.UnknownError;
        }
    }
}
