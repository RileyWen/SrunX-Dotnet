using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcService;

namespace SrunX
{
    public class Program
    {
        private static async Task Main(string[] args)
        {
            var mailboxName = GetMailboxName(args);

            Console.WriteLine($"Creating client to mailbox '{mailboxName}'");
            Console.WriteLine();

            var channel = GrpcChannel.ForAddress("https://localhost:5001");
            var client = new Mailer.MailerClient(channel);

            Console.WriteLine("Client created");
            Console.WriteLine("Press escape to disconnect. Press any other key to forward mail.");

            using (var call = client.Mailbox(headers: new Metadata { new Metadata.Entry("mailbox-name", mailboxName) }))
            {
                var responseTask = Task.Run(async () =>
                {
                    await foreach (var message in call.ResponseStream.ReadAllAsync())
                    {
                        Console.ForegroundColor = message.Reason == MailboxMessage.Types.Reason.Received ? ConsoleColor.White : ConsoleColor.Green;
                        Console.WriteLine();
                        Console.WriteLine(message.Reason == MailboxMessage.Types.Reason.Received ? "Mail received" : "Mail forwarded");
                        Console.WriteLine($"New mail: {message.New}, Forwarded mail: {message.Forwarded}");
                        Console.ResetColor();
                    }
                });

                while (true)
                {
                    var result = Console.ReadKey(intercept: true);
                    if (result.Key == ConsoleKey.Escape)
                    {
                        break;
                    }

                    await call.RequestStream.WriteAsync(new ForwardMailMessage());
                }

                Console.WriteLine("Disconnecting");
                await call.RequestStream.CompleteAsync();
                await responseTask;
            }

            Console.WriteLine("Disconnected. Press any key to exit.");
            Console.ReadKey();
        }

        private static string GetMailboxName(IReadOnlyList<string> args)
        {
            if (args.Count >= 1) return args[0];
            
            Console.WriteLine("No mailbox name provided. Using default name. Usage: dotnet run <name>.");
            return "DefaultMailbox";

        }
    }
}
