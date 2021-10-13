using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
            return Parser.Default.ParseArguments<CmdOptions>(args)
                .MapResult(RealMain, _ => 1);
        }

        private static int RealMain(CmdOptions opts)
        {
            if (opts.Partition == "")
            {
                Log.Error("Invalid Partition name!");
                return 1;
            }

            var requiredAllocatableResource = new AllocatableResource();
            requiredAllocatableResource.CpuCoreLimit = opts.CpuCore;

            var memoryRegex = new Regex(@"(\d+)([KMBG])");
            var memoryMatch = memoryRegex.Match(opts.Memory);
            if (!memoryMatch.Success)
            {
                Log.Error(@"Memory should follow the pattern: \d+[MKBG]");
                return 1;
            }

            requiredAllocatableResource.MemoryLimitBytes = (memoryMatch.Groups[2].Value) switch
            {
                "B" => ulong.Parse(memoryMatch.Groups[1].Value),
                "K" => ulong.Parse(memoryMatch.Groups[1].Value) * 1024,
                "M" => ulong.Parse(memoryMatch.Groups[1].Value) * 1024 * 1024,
                "G" => ulong.Parse(memoryMatch.Groups[1].Value) * 1024 * 1024 * 1024,
                _ => 0 // Impossible case
            };

            var timeRegex = new Regex(@"(\d+)([SMH])");
            var timeMatch = timeRegex.Match(opts.TimeLimit);
            if (!timeMatch.Success)
            {
                Log.Error(@"TimeLimit should follow the pattern: \d+[MKBG]");
                return 1;
            }

            UInt64 timeLimitSec = timeMatch.Groups[2].Value switch
            {
                "S" => UInt64.Parse(timeMatch.Groups[1].Value),
                "M" => UInt64.Parse(timeMatch.Groups[1].Value) * 60,
                "H" => UInt64.Parse(timeMatch.Groups[1].Value) * 60 * 60,
                _ => 0
            };

            requiredAllocatableResource.MemorySwLimitBytes = requiredAllocatableResource.MemoryLimitBytes;
            Resources requiredResource = new Resources
            {
                AllocatableResource = requiredAllocatableResource
            };

            Log.Debug($"{requiredAllocatableResource}, {opts.ServerAddr}, {string.Join(" ", opts.RemoteCmd)}");

            CtlXdClient ctlXdClient = new CtlXdClient("http://" + opts.ServerAddr);

            bool ok;
            UInt32? taskId;
            InteractiveTaskAllocationDetail allocationDetail;

            (ok, taskId) = ctlXdClient.AllocateInteractiveTask(opts.Partition, requiredResource, timeLimitSec);
            if (!ok)
            {
                Log.Info("Allocation failed.");
                return 1;
            }

            while (true)
            {
                (ok, allocationDetail) = ctlXdClient.QueryInteractiveTaskAllocDetail(taskId.Value);
                if (ok)
                    break;

                Log.Info("No enough resource is available now. Waiting...");
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            XdClient xdClient = new XdClient();
            ok = xdClient.ExecuteTask(taskId.Value, allocationDetail, opts.RemoteCmd.ToArray());

            if (!ok)
                return 1;

            return 0;
        }

        private static readonly log4net.ILog Log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType);
    }
}