/// Descriptive statistics (Document 7 §13.5) — e.g. spotting the jittery dark-subnet relay in Crawler data.
/// Functions take float or int series; an empty series raises ArgumentError. Cost: n (sorting functions n log n).
module Std.Stats;

float Mean(Iterable<float> values);
@Native("Std.Stats.MeanInt") float Mean(Iterable<int> values);
float Median(Iterable<float> values);
@Native("Std.Stats.MedianInt") float Median(Iterable<int> values);
/// The most frequent value (the smallest on ties).
float Mode(Iterable<float> values);
@Native("Std.Stats.ModeInt") float Mode(Iterable<int> values);
/// Population variance.
float Variance(Iterable<float> values);
@Native("Std.Stats.VarianceInt") float Variance(Iterable<int> values);
float SampleVariance(Iterable<float> values);
@Native("Std.Stats.SampleVarianceInt") float SampleVariance(Iterable<int> values);
/// Population standard deviation.
float StdDev(Iterable<float> values);
@Native("Std.Stats.StdDevInt") float StdDev(Iterable<int> values);
float SampleStdDev(Iterable<float> values);
@Native("Std.Stats.SampleStdDevInt") float SampleStdDev(Iterable<int> values);
/// Linear-interpolated percentile, p in [0, 100].
float Percentile(Iterable<float> values, float p);
@Native("Std.Stats.PercentileInt") float Percentile(Iterable<int> values, float p);
Summary Describe(Iterable<float> values);
@Native("Std.Stats.DescribeInt") Summary Describe(Iterable<int> values);
/// Equal-width bins between the minimum and maximum.
Histogram HistogramOf(Iterable<float> values, int bins);
@Native("Std.Stats.HistogramOfInt") Histogram HistogramOf(Iterable<int> values, int bins);
/// Pearson correlation coefficient of two equally long series.
float Correlation(float[] xs, float[] ys);
/// Least-squares fit y = slope·x + intercept, with the coefficient of determination.
Regression LinearRegression(float[] xs, float[] ys);
/// Simple moving average over a sliding window (length n − window + 1).
float[] MovingAverage(float[] values, int window);
/// Exponentially weighted moving average with smoothing factor alpha in (0, 1].
float[] ExponentialMovingAverage(float[] values, float alpha);
float[] ZScores(float[] values);
/// Indices of values more than threshold standard deviations from the mean.
int[] Outliers(float[] values, float threshold);
/// Indices outside [Q1 − 1.5·IQR, Q3 + 1.5·IQR] (Tukey's fences).
int[] IqrOutliers(float[] values);

class Summary {
    int Count { get; }
    float Min { get; }
    float Max { get; }
    float Mean { get; }
    float Median { get; }
    float StdDev { get; }
    float Q1 { get; }
    float Q3 { get; }
    string ToString();
}

class Histogram {
    /// bins + 1 edges; bin i covers [Edges[i], Edges[i+1]) (the last bin includes the maximum).
    float[] Edges { get; }
    int[] Counts { get; }
    int BinOf(float value);
    string ToString();
}

class Regression {
    float Slope { get; }
    float Intercept { get; }
    float RSquared { get; }
    float Predict(float x);
    string ToString();
}

/// Streaming statistics (Welford): constant memory, numerically stable.
class RunningStats {
    RunningStats();
    void Add(float value);
    int Count { get; }
    float Mean { get; }
    float Variance { get; }
    float StdDev { get; }
    float Min { get; }
    float Max { get; }
    void Reset();
    string ToString();
}
