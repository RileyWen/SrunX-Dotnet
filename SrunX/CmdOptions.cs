using System.Collections.Generic;
using CommandLine;

namespace SrunX
{
    public class CmdOptions
    {
        [Option('c', Required = true)] public uint CpuCore { get; set; }
        [Option('m', Required = true)] public string Memory { get; set; }
        [Option('s', Required = true)] public string ServerAddr { get; set; }
        [Option('p', Required = true)] public string Partition { get; set; }
        [Value(0, Min = 1, Required = true)] public IEnumerable<string> RemoteCmd { get; set; }
    }
}