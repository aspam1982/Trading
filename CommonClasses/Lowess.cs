using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using Tinkoff.InvestApi.V1;

/// <summary>
/// Небольшая реализация LOWESS-сглаживания для рядов, где нужна локальная регрессия
/// без внешней зависимости на статистический пакет.
/// </summary>
public class Lowess
{
    /// <summary>
    /// Выполняет LOWESS сглаживание
    /// </summary>
    /// <param name="x">Входные x-координаты</param>
    /// <param name="y">Входные y-координаты</param>
    /// <param name="span">Доля точек в окне (0-1)</param>
    /// <param name="degree">Степень полинома (1 или 2)</param>
    /// <returns>Массив сглаженных y-значений</returns>
    public static double[] Smooth(double[] x, double[] y, int span = 20, int degree = 2)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("Arrays x and y must have the same length");

        int n = x.Length;
        double[] ySmooth = new double[n];
        int windowSize = span;
        if (windowSize % 2 == 0) windowSize++; // делаем размер окна нечетным

        for (int i = 0; i < n; i++)
        {
            // Определяем окно
            int halfWindow = windowSize / 2;
            int start = Math.Max(0, i - halfWindow);
            int end = Math.Min(n - 1, i + halfWindow);
            int actualWindowSize = end - start + 1;

            double[] xWindow = new double[actualWindowSize];
            double[] yWindow = new double[actualWindowSize];
            double[] weights = new double[actualWindowSize];

            // Заполняем окно и вычисляем веса
            double maxDistance = x[end] - x[start];
            for (int j = 0; j < actualWindowSize; j++)
            {
                xWindow[j] = x[start + j];
                yWindow[j] = y[start + j];
                double distance = Math.Abs(x[i] - xWindow[j]);
                weights[j] = WeightFunction(distance / maxDistance);
            }

            // Выполняем взвешенную полиномиальную регрессию
            ySmooth[i] = degree == 1
                ? LinearRegression(xWindow, yWindow, weights, x[i])
                : QuadraticRegression(xWindow, yWindow, weights, x[i]);
        }

        return ySmooth;
    }

    /// <summary>
    /// Функция веса (трикубическая)
    /// </summary>
    private static double WeightFunction(double x)
    {
        double w = 1.0 - Math.Abs(x * x * x);
        return w * w * w;
    }

    /// <summary>
    /// Линейная регрессия с весами
    /// </summary>
    private static double LinearRegression(double[] x, double[] y, double[] weights, double xPred)
    {
        double sumW = weights.Sum();
        double sumWX = 0, sumWY = 0, sumWXX = 0, sumWXY = 0;

        for (int i = 0; i < x.Length; i++)
        {
            double w = weights[i];
            sumWX += w * x[i];
            sumWY += w * y[i];
            sumWXX += w * x[i] * x[i];
            sumWXY += w * x[i] * y[i];
        }

        double denom = sumW * sumWXX - sumWX * sumWX;
        if (Math.Abs(denom) < 1e-10)
            return y.Average(); // защита от деления на ноль

        double b = (sumW * sumWXY - sumWX * sumWY) / denom;
        double a = (sumWY - b * sumWX) / sumW;

        return a + b * xPred;
    }

    /// <summary>
    /// Квадратичная регрессия с весами
    /// </summary>
    private static double QuadraticRegression(double[] x, double[] y, double[] weights, double xPred)
    {
        int n = x.Length;
        double[,] A = new double[3, 3];
        double[] B = new double[3];

        for (int i = 0; i < n; i++)
        {
            double w = weights[i];
            double x1 = x[i];
            double x2 = x1 * x1;
            double yw = y[i] * w;

            A[0, 0] += w;
            A[0, 1] += w * x1;
            A[0, 2] += w * x2;
            A[1, 1] += w * x1 * x1;
            A[1, 2] += w * x1 * x2;
            A[2, 2] += w * x2 * x2;
            B[0] += yw;
            B[1] += yw * x1;
            B[2] += yw * x2;
        }

        A[1, 0] = A[0, 1];
        A[2, 0] = A[0, 2];
        A[2, 1] = A[1, 2];

        // Решаем систему уравнений методом Гаусса
        double[] coeffs = SolveLinearSystem(A, B);
        return coeffs[0] + coeffs[1] * xPred + coeffs[2] * xPred * xPred;
    }

    /// <summary>
    /// Решение системы линейных уравнений методом Гаусса
    /// </summary>
    private static double[] SolveLinearSystem(double[,] A, double[] B)
    {
        int n = 3;
        double[] result = new double[n];
        double[,] matrix = (double[,])A.Clone();
        double[] b = (double[])B.Clone();

        for (int i = 0; i < n; i++)
        {
            // Поиск главного элемента
            int maxRow = i;
            for (int k = i + 1; k < n; k++)
                if (Math.Abs(matrix[k, i]) > Math.Abs(matrix[maxRow, i]))
                    maxRow = k;

            // Перестановка строк
            for (int k = i; k < n; k++)
            {
                double tmp = matrix[maxRow, k];
                matrix[maxRow, k] = matrix[i, k];
                matrix[i, k] = tmp;
            }
            double tmpB = b[maxRow];
            b[maxRow] = b[i];
            b[i] = tmpB;

            // Прямой ход
            for (int k = i + 1; k < n; k++)
            {
                double c = -matrix[k, i] / matrix[i, i];
                for (int j = i; j < n; j++)
                    matrix[k, j] += c * matrix[i, j];
                b[k] += c * b[i];
            }
        }

        // Обратный ход
        for (int i = n - 1; i >= 0; i--)
        {
            result[i] = b[i] / matrix[i, i];
            for (int k = i - 1; k >= 0; k--)
                b[k] -= matrix[k, i] * result[i];
        }

        return result;
    }
}
/// <summary>
/// Инкрементальный расчет EMA для потокового добавления значений.
/// </summary>
public class EmaCalculator
{
    private List<double> values = new List<double>();
    public IEnumerable<double> Values { get => values.AsEnumerable(); }
    private List<double> emas { get; } = new List<double>();
    public IEnumerable<double> EMAs { get => emas.AsEnumerable(); }
    public int EMAOrder { get; set; } = 20;
    public double AddValue(double value)
    {
        double LastEMA = emas.Any() ? emas.Last() : value;
        values.Add(value);
        double EMA = (value - LastEMA) * 2 / (EMAOrder + 1) + LastEMA;
        emas.Add(EMA);
        return EMA;
    }
    public EmaCalculator(IEnumerable<double> source, int EMAOrder)
    {
        this.EMAOrder = EMAOrder;
        foreach (var val in source)
            AddValue(val);
    }
    public void Clear()
    {
        emas.Clear();
        values.Clear();
    }
}
