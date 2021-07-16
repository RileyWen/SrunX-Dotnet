using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Learning
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await foreach (var dataPoint in FetchIotData())
            {
                Console.WriteLine(dataPoint);
            }

            Console.ReadLine();
        }

        static async IAsyncEnumerable<int> FetchIotData()
        {
            for (int i = 1; i <= 10; i++)
            {
                await Task.Delay(1000); //Simulate waiting for data to come through. 
                yield return i;
            }
        }
    }
}