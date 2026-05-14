using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace CommonClasses;

/// <summary>
/// Генетический оптимизатор параметров торговых стратегий.
/// Пользователь передает пространство параметров и функцию оценки, а оптимизатор
/// подбирает комбинацию с лучшим fitness с учетом прибыли, просадки и частоты сделок.
/// </summary>
public class TradingRobotOptimizer
{
    public delegate (double Profit, double Drawdown, double DealsFrequency) FitnessFunction(Dictionary<string, object> parameters);
    public delegate double FitnessCalculator(double profit, double drawdown, double dealsfrequency);

    public enum ParameterType { Integer, Double, Boolean }

    /// <summary>
    /// Описание одного оптимизируемого параметра: имя, тип, диапазон и необязательный шаг.
    /// </summary>
    public class ParameterDefinition
    {
        private object? minValue;
        private object? maxValue;

        public string Name { get; set; } = "";
        public ParameterType Type { get; set; }

        public object MinValue
        {
            get
            {
                if (minValue == null)
                    minValue = Type switch
                    {
                        ParameterType.Boolean => false,
                        ParameterType.Integer => 0,
                        ParameterType.Double => 0d,
                        _ => 0
                    };
                return minValue;
            }
            set => minValue = value;
        }

        public object MaxValue
        {
            get
            {
                if (maxValue == null)
                    maxValue = Type switch
                    {
                        ParameterType.Boolean => true,
                        ParameterType.Integer => 1,
                        ParameterType.Double => 1d,
                        _ => 1
                    };
                return maxValue;
            }
            set => maxValue = value;
        }

        public object? StepValue { get; set; }
    }

    /// <summary>
    /// Конкретный набор параметров и результат его оценки.
    /// </summary>
    public class RobotParameters
    {
        public Dictionary<string, object> Parameters { get; set; }
        public double Fitness { get; set; }
        public double Profit { get; set; }
        public double Drawdown { get; set; }
        public double DealsFrequency { get; set; }

        public RobotParameters(Dictionary<string, object> parameters)
        {
            Parameters = new Dictionary<string, object>(parameters);
        }

        public RobotParameters Clone()
        {
            return new RobotParameters(new Dictionary<string, object>(Parameters))
            {
                Fitness = Fitness,
                Profit = Profit,
                Drawdown = Drawdown,
                DealsFrequency = DealsFrequency
            };
        }
    }

    private class EvaluatedParameters
    {
        public double Fitness { get; set; }
        public double Profit { get; set; }
        public double Drawdown { get; set; }
        public double DealsFrequency { get; set; }
    }

    private readonly int _populationSize;
    private readonly double _mutationRate;
    private readonly double _crossoverRate;
    private readonly int _maxGenerations;
    private readonly Random _random;
    private readonly List<ParameterDefinition> _parameterDefinitions;

    private double _currentMutationRate;
    private readonly bool _multiThreaded;

    private RobotParameters? _bestHistoricalIndividual;
    private int _stagnationCount = 0;

    private readonly ConcurrentDictionary<string, EvaluatedParameters> _evaluationCache = new();

    public TradingRobotOptimizer(
        int populationSize,
        double mutationRate,
        double crossoverRate,
        int maxGenerations,
        List<ParameterDefinition> parameterDefinitions,
        bool multiThreaded = false)
    {
        _populationSize = populationSize;
        _mutationRate = mutationRate;
        _crossoverRate = crossoverRate;
        _maxGenerations = maxGenerations;
        _parameterDefinitions = parameterDefinitions;

        _random = new Random();
        _currentMutationRate = mutationRate;
        _multiThreaded = multiThreaded;
    }

    public static RobotParameters Optimize(
        List<ParameterDefinition> parameterDefinitions,
        FitnessFunction evaluateStrategy,
        int populationSize,
        int maxGenerations,
        float mutationRate = 0.3f,
        float crossoverRate = 0.8f,
        bool multiThreaded = false,
        FitnessCalculator? fitnessCalculator = null)
    {
        var optimizer = new TradingRobotOptimizer(
            populationSize,
            mutationRate,
            crossoverRate,
            maxGenerations,
            parameterDefinitions,
            multiThreaded);

        return optimizer.Optimize(evaluateStrategy, fitnessCalculator);
    }

    public RobotParameters Optimize(FitnessFunction evaluateStrategy, FitnessCalculator? fitnessCalculator = null)
    {
        _evaluationCache.Clear();
        _stagnationCount = 0;
        _bestHistoricalIndividual = null;

        var population = InitializePopulation();

        for (int generation = 0; generation < _maxGenerations; generation++)
        {
            EvaluatePopulation(population, evaluateStrategy, fitnessCalculator);

            var currentBest = population.OrderByDescending(i => i.Fitness).First();
            if (_bestHistoricalIndividual == null || currentBest.Fitness > _bestHistoricalIndividual.Fitness)
                _bestHistoricalIndividual = currentBest.Clone();

            var sb = new StringBuilder();
            sb.AppendLine($"Generation {generation + 1}");
            sb.AppendLine(
                $"Historical Best - Fitness: {_bestHistoricalIndividual.Fitness:0.0000}, " +
                $"Profit: {_bestHistoricalIndividual.Profit:P2}, " +
                $"Drawdown: {_bestHistoricalIndividual.Drawdown:P2}, " +
                $"DealsFrequency: {_bestHistoricalIndividual.DealsFrequency:F2}");
            sb.AppendLine($"Parameters: [{string.Join(", ", _bestHistoricalIndividual.Parameters.Select(p => $"{p.Key}: {p.Value}"))}]");
            sb.AppendLine(new string('-', 80));
            Console.Write(sb.ToString());
            Debug.Write(sb.ToString());

            // При стагнации мутация усиливается, чтобы выйти из локального максимума.
            CheckStagnation(population);

            var newPopulation = new List<RobotParameters> { _bestHistoricalIndividual.Clone() };

            while (newPopulation.Count < _populationSize)
            {
                var parent1 = TournamentSelection(population);
                var parent2 = TournamentSelection(population);

                var (child1, child2) = Crossover(parent1, parent2);
                Mutate(child1);
                Mutate(child2);

                newPopulation.Add(child1);
                if (newPopulation.Count < _populationSize)
                    newPopulation.Add(child2);
            }

            population = newPopulation;
        }

        return _bestHistoricalIndividual!;
    }

    private void EvaluatePopulation(
        List<RobotParameters> population,
        FitnessFunction evaluateStrategy,
        FitnessCalculator? fitnessCalculator)
    {
        var tasks = new List<Task>();

        foreach (var individual in population)
        {
            NormalizeParametersInPlace(individual.Parameters);
            var paramKey = GetParametersKey(individual.Parameters);

            if (_evaluationCache.TryGetValue(paramKey, out var cached))
            {
                individual.Fitness = cached.Fitness;
                individual.Profit = cached.Profit;
                individual.Drawdown = cached.Drawdown;
                individual.DealsFrequency = cached.DealsFrequency;
                continue;
            }

            var t = Task.Run(() =>
            {
                var (profit, drawdown, dealsfrequency) = evaluateStrategy(individual.Parameters);

                individual.Profit = profit;
                individual.Drawdown = drawdown;
                individual.DealsFrequency = dealsfrequency;
                individual.Fitness = (fitnessCalculator ?? CalculateFitness)(profit, drawdown, dealsfrequency);

                _evaluationCache[paramKey] = new EvaluatedParameters
                {
                    Fitness = individual.Fitness,
                    Profit = individual.Profit,
                    Drawdown = individual.Drawdown,
                    DealsFrequency = individual.DealsFrequency
                };
            });

            if (!_multiThreaded)
                t.Wait();

            tasks.Add(t);
        }

        if (_multiThreaded)
            Task.WaitAll(tasks.ToArray());
    }

    private string GetParametersKey(Dictionary<string, object> parameters)
    {
        var parts = new List<string>();
        foreach (var param in _parameterDefinitions.OrderBy(p => p.Name))
        {
            var value = NormalizeParameterValue(parameters[param.Name], param);
            var text = value switch
            {
                double d => d.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            };
            parts.Add($"{param.Name}:{text}");
        }
        return string.Join("|", parts);
    }

    private void CheckStagnation(List<RobotParameters> population)
    {
        var currentBest = population.OrderByDescending(i => i.Fitness).First();

        if (_bestHistoricalIndividual != null && currentBest.Fitness <= _bestHistoricalIndividual.Fitness * 1.001)
        {
            _stagnationCount++;
            if (_stagnationCount > 2)
            {
                int replaceCount = (int)(_populationSize * 0.3);
                for (int i = 1; i < replaceCount; i++)
                    population[population.Count - i] = CreateRandomIndividual();

                _stagnationCount = 0;
                Console.WriteLine("!!! Population partially restarted due to stagnation !!!");
            }
        }
        else
        {
            _stagnationCount = 0;
        }

        double diversity = CalculateDiversity(population);
        _currentMutationRate = diversity < 0.2 ? Math.Min(_mutationRate * 2, 0.5) : _mutationRate;
    }

    private List<RobotParameters> InitializePopulation()
    {
        var population = new List<RobotParameters>();
        for (int i = 0; i < _populationSize; i++)
            population.Add(i < _populationSize * 0.1 ? CreateRandomIndividual() : CreateExtremeValuesIndividual());
        return population;
    }

    private RobotParameters CreateRandomIndividual()
    {
        var parameters = new Dictionary<string, object>();
        foreach (var paramDef in _parameterDefinitions)
            parameters[paramDef.Name] = GenerateRandomParameter(paramDef);
        return new RobotParameters(parameters);
    }

    private RobotParameters CreateExtremeValuesIndividual()
    {
        var parameters = new Dictionary<string, object>();
        foreach (var paramDef in _parameterDefinitions)
        {
            parameters[paramDef.Name] = _random.NextDouble() < 0.5 ? NormalizeParameterValue(paramDef.MinValue, paramDef) : NormalizeParameterValue(paramDef.MaxValue, paramDef);
            if (_random.NextDouble() < 0.3)
                parameters[paramDef.Name] = GenerateRandomParameter(paramDef);
        }
        return new RobotParameters(parameters);
    }

    private object GenerateRandomParameter(ParameterDefinition paramDef)
    {
        return paramDef.Type switch
        {
            ParameterType.Integer => GenerateRandomInt(paramDef),
            ParameterType.Double => GenerateRandomDouble(paramDef),
            ParameterType.Boolean => _random.NextDouble() > 0.5,
            _ => throw new ArgumentException($"Unknown parameter type: {paramDef.Type}")
        };
    }

    private int GenerateRandomInt(ParameterDefinition paramDef)
    {
        var min = Convert.ToInt32(paramDef.MinValue);
        var max = Convert.ToInt32(paramDef.MaxValue);
        var step = paramDef.StepValue is null ? 1 : Math.Max(1, Convert.ToInt32(paramDef.StepValue));
        var count = ((max - min) / step) + 1;
        var idx = _random.Next(0, count);
        return min + idx * step;
    }

    private double GenerateRandomDouble(ParameterDefinition paramDef)
    {
        var min = Convert.ToDouble(paramDef.MinValue);
        var max = Convert.ToDouble(paramDef.MaxValue);
        var step = paramDef.StepValue is null ? 0d : Convert.ToDouble(paramDef.StepValue);

        if (step > 0d)
        {
            var count = Math.Max(1, (int)Math.Round((max - min) / step));
            var idx = _random.Next(0, count + 1);
            return NormalizeDouble(min + idx * step, paramDef);
        }

        var value = _random.NextDouble() * (max - min) + min;
        return NormalizeDouble(value, paramDef);
    }

    private RobotParameters TournamentSelection(List<RobotParameters> population)
    {
        int tournamentSize = Math.Max(5, Convert.ToInt32(population.Count / 4));
        var tournament = population.OrderBy(_ => _random.Next()).Take(tournamentSize).ToList();
        tournament.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

        double r = _random.NextDouble();
        return r switch
        {
            < 0.8 => tournament[0],
            < 0.95 => tournament[1],
            _ => tournament[Math.Min(2, tournament.Count - 1)]
        };
    }

    private (RobotParameters, RobotParameters) Crossover(RobotParameters parent1, RobotParameters parent2)
    {
        if (_random.NextDouble() > _crossoverRate)
            return (parent1.Clone(), parent2.Clone());

        var child1Params = new Dictionary<string, object>();
        var child2Params = new Dictionary<string, object>();

        foreach (var paramDef in _parameterDefinitions)
        {
            if (_random.NextDouble() < 0.5)
            {
                child1Params[paramDef.Name] = parent1.Parameters[paramDef.Name];
                child2Params[paramDef.Name] = parent2.Parameters[paramDef.Name];
            }
            else
            {
                child1Params[paramDef.Name] = parent2.Parameters[paramDef.Name];
                child2Params[paramDef.Name] = parent1.Parameters[paramDef.Name];
            }
        }

        NormalizeParametersInPlace(child1Params);
        NormalizeParametersInPlace(child2Params);

        return (new RobotParameters(child1Params), new RobotParameters(child2Params));
    }

    private void Mutate(RobotParameters individual)
    {
        foreach (var paramDef in _parameterDefinitions)
        {
            if (_random.NextDouble() < _currentMutationRate)
                individual.Parameters[paramDef.Name] = MutateParameter(individual.Parameters[paramDef.Name], paramDef);
        }

        NormalizeParametersInPlace(individual.Parameters);
    }

    private object MutateParameter(object value, ParameterDefinition paramDef)
    {
        var rnd = _random.NextDouble();

        return paramDef.Type switch
        {
            ParameterType.Integer => MutateInt(value, paramDef, rnd),
            ParameterType.Double => MutateDouble(value, paramDef, rnd),
            ParameterType.Boolean => _random.NextDouble() < 0.3 ? !Convert.ToBoolean(value) : value,
            _ => throw new ArgumentException($"Unknown parameter type: {paramDef.Type}")
        };
    }

    private object MutateInt(object value, ParameterDefinition paramDef, double rnd)
    {
        int intValue = Convert.ToInt32(value);
        int minInt = Convert.ToInt32(paramDef.MinValue);
        int maxInt = Convert.ToInt32(paramDef.MaxValue);
        int step = paramDef.StepValue is null ? 1 : Math.Max(1, Convert.ToInt32(paramDef.StepValue));

        int spanSteps = Math.Max(1, (maxInt - minInt) / step);
        int mutationSteps = Convert.ToInt32(Math.Ceiling(spanSteps * _random.NextDouble() * 0.2)) * (_random.NextDouble() > 0.5 ? 1 : -1);

        if (rnd < 0.1)
            mutationSteps = Convert.ToInt32(Math.Ceiling(spanSteps * _random.NextDouble() * 0.5)) * (_random.NextDouble() > 0.5 ? 1 : -1);
        else if (rnd < 0.05)
            mutationSteps = Convert.ToInt32(Math.Ceiling(spanSteps * _random.NextDouble())) * (_random.NextDouble() > 0.5 ? 1 : -1);

        var mutated = intValue + mutationSteps * step;
        mutated = Math.Clamp(mutated, minInt, maxInt);

        if (step > 1)
            mutated = minInt + ((mutated - minInt) / step) * step;

        return mutated;
    }

    private object MutateDouble(object value, ParameterDefinition paramDef, double rnd)
    {
        double doubleValue = Convert.ToDouble(value);
        double minDouble = Convert.ToDouble(paramDef.MinValue);
        double maxDouble = Convert.ToDouble(paramDef.MaxValue);

        double mutation = (_random.NextDouble() - 0.5) * (maxDouble - minDouble) * 0.2;
        if (rnd < 0.1)
            mutation = (_random.NextDouble() - 0.5) * (maxDouble - minDouble) * 0.5;
        else if (rnd < 0.05)
            mutation = (_random.NextDouble() - 0.5) * (maxDouble - minDouble);

        return NormalizeDouble(Math.Clamp(doubleValue + mutation, minDouble, maxDouble), paramDef);
    }

    private static double CalculateFitness(double profit, double drawdown, double dealsfrequency)
    {
        return profit * (1.0d - drawdown / 2d);
    }

    private double CalculateDiversity(List<RobotParameters> population)
    {
        if (population.Count < 2) return 1.0;

        double totalDistance = 0;
        int sampleSize = Math.Min(10, population.Count);
        int comparisons = 0;

        for (int i = 0; i < sampleSize; i++)
        {
            for (int j = i + 1; j < sampleSize; j++)
            {
                totalDistance += ParameterDistance(population[i].Parameters, population[j].Parameters);
                comparisons++;
            }
        }

        return comparisons == 0 ? 1.0 : totalDistance / comparisons;
    }

    private double ParameterDistance(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        double distance = 0;

        foreach (var paramDef in _parameterDefinitions)
        {
            var name = paramDef.Name;
            var av = NormalizeParameterValue(a[name], paramDef);
            var bv = NormalizeParameterValue(b[name], paramDef);

            if (av is int ai && bv is int bi)
            {
                var denom = Math.Max(1d, Convert.ToInt32(paramDef.MaxValue) - Convert.ToInt32(paramDef.MinValue));
                distance += Math.Abs(ai - bi) / denom;
            }
            else if (av is double ad && bv is double bd)
            {
                var denom = Math.Max(1e-12, Convert.ToDouble(paramDef.MaxValue) - Convert.ToDouble(paramDef.MinValue));
                distance += Math.Abs(ad - bd) / denom;
            }
            else if (av is bool ab && bv is bool bb)
            {
                distance += ab == bb ? 0 : 1;
            }
        }

        return distance / _parameterDefinitions.Count;
    }

    private void NormalizeParametersInPlace(Dictionary<string, object> parameters)
    {
        foreach (var def in _parameterDefinitions)
        {
            if (parameters.TryGetValue(def.Name, out var value))
                parameters[def.Name] = NormalizeParameterValue(value, def);
        }
    }

    private object NormalizeParameterValue(object value, ParameterDefinition def)
    {
        return def.Type switch
        {
            ParameterType.Integer => NormalizeInt(Convert.ToInt32(value), def),
            ParameterType.Double => NormalizeDouble(Convert.ToDouble(value), def),
            ParameterType.Boolean => Convert.ToBoolean(value),
            _ => value
        };
    }

    private int NormalizeInt(int value, ParameterDefinition def)
    {
        var min = Convert.ToInt32(def.MinValue);
        var max = Convert.ToInt32(def.MaxValue);
        var step = def.StepValue is null ? 1 : Math.Max(1, Convert.ToInt32(def.StepValue));

        value = Math.Clamp(value, min, max);
        if (step > 1)
            value = min + (int)Math.Round((value - min) / (double)step) * step;

        return Math.Clamp(value, min, max);
    }

    private double NormalizeDouble(double value, ParameterDefinition def)
    {
        var min = Convert.ToDouble(def.MinValue);
        var max = Convert.ToDouble(def.MaxValue);
        value = Math.Clamp(value, min, max);

        if (def.StepValue is null)
            return value;

        var step = Convert.ToDouble(def.StepValue);
        if (step <= 0d)
            return value;

        var steps = Math.Round((value - min) / step);
        var normalized = min + steps * step;
        normalized = Math.Clamp(normalized, min, max);
        return normalized;
    }
}
