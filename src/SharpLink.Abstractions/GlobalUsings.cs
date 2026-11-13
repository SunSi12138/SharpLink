global using System;
global using System.Buffers;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;

global using System.Runtime.InteropServices;
global using System.IO.Pipelines;
global using System.Threading.Channels;
global using System.Diagnostics;
global using System.Collections.Concurrent;

global using SharpLink.Sdk;

#if SHARPLINK_775_STATIC_SLICE
global using IRpcChannel = SharpLink.Abstractions.IRpcContractChannel;
#endif
