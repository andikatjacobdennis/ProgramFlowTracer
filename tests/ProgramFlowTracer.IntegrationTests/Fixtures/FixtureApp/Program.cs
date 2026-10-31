using System;

namespace FixtureApp
{
    public class Calculator
    {
        public int Add(int a, int b) => a + b;

        public int Divide(int a, int b)
        {
            if (b == 0)
            {
                throw new DivideByZeroException("cannot divide by zero");
            }

            return a / b;
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var calc = new Calculator();
            Console.WriteLine($"3 + 4 = {calc.Add(3, 4)}");
            Console.WriteLine($"10 / 2 = {calc.Divide(10, 2)}");
        }
    }
}
