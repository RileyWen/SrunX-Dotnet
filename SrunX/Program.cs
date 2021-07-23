using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Grpc.Core;
using Grpc.Net.Client;
using SlurmxGrpc;

namespace SrunX
{
    public class Program
    {
        private static int Main(string[] args)
        {
            Console.CancelKeyPress += GrpcClient.OnCtrlC;
            return Parser.Default.ParseArguments<CmdOptions>(args)
                .MapResult(RealMain, _ => 1);
        }

        private static int RealMain(CmdOptions opts)
        {
            if (opts.Partition == "")
            {
                log.Error("Invalid Partition name!");
                return 1;
            }

            var requiredResource = new AllocatableResource();
            requiredResource.CpuCoreLimit = opts.CpuCore;

            var memoryRegex = new Regex(@"(\d+)([KMBG])");
            var match = memoryRegex.Match(opts.Memory);
            if (!match.Success)
            {
                log.Error(@"Memory should follow the pattern: \d+[MKBG]");
                return 1;
            }

            requiredResource.MemoryLimitBytes = (match.Groups[2].Value) switch
            {
                "B" => ulong.Parse(match.Groups[1].Value),
                "K" => ulong.Parse(match.Groups[1].Value) * 1024,
                "M" => ulong.Parse(match.Groups[1].Value) * 1024 * 1024,
                "G" => ulong.Parse(match.Groups[1].Value) * 1024 * 1024 * 1024,
                _ => 0 // Impossible case
            };

            requiredResource.MemorySwLimitBytes = requiredResource.MemoryLimitBytes;

            log.Debug($"{requiredResource}, {opts.ServerAddr}, {string.Join(" ", opts.RemoteCmd)}");

            if (!GrpcClient.TryAllocateResource(opts.Partition, requiredResource, "http://" + opts.ServerAddr,
                out var resourceInfo))
                return 1;

            if (!GrpcClient.ExecuteTask(resourceInfo, opts.RemoteCmd.ToArray()))
                return 1;

            return 0;
        }

        private static log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);
    }
}