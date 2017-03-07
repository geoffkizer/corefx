using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    public static class SlimTaskTest
    {
        public static async SlimTask<int> MainAsync()
        {
            int i;

            i = await DoStuff(1);
            Console.WriteLine($"Result = {i}");

            i = await DoStuff(10);
            Console.WriteLine($"Result = {i}");

            return 0;
        }

        public static void MainSync()
        {
            var a = MainAsync().GetAwaiter();
            a.Wait();
        }

        public static async SlimTask<int> DoStuff(int i)
        {
            int a = await DoMoreStuff(1);
            int b = await DoMoreStuff(2);
            int c = await DoMoreStuff(3);

            return a + b + c + i;
        }

        public static async SlimTask<int> DoMoreStuff(int i)
        {
            await Task.Delay(3000);
            return i;
        }

        public static int DoStuffSync(int i)
        {
            var a = DoStuff(i).GetAwaiter();
            a.Wait();
            return a.GetResult();
        }

        public static int DoMoreStuffSync(int i)
        {
            var a = DoMoreStuff(i).GetAwaiter();
            a.Wait();
            return a.GetResult();
        }

        public static async SlimTask<int> DoMoreStuff2(int i)
        {
            Console.WriteLine("Before first await");
            await Task.Delay(3000);
            Console.WriteLine("After first await, before second await");
            await Task.Delay(3000);
            Console.WriteLine("After second await");
            return i;
        }

        public static int DoMoreStuff2Sync(int i)
        {
            var a = DoMoreStuff2(i).GetAwaiter();
            a.Wait();
            return a.GetResult();
        }
    }
}
