﻿using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MLOps.NET.Azure.IntegrationTests.Constants;
using MLOps.NET.Azure.Storage;
using MLOps.NET.Storage;
using MLOps.NET.Tests.Common.Configuration;
using MLOps.NET.Tests.Common.Data;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MLOps.NET.Azure.IntegrationTests
{
    [TestClass]
    [TestCategory("IntegrationTestCosmosDb")]
    public class MetaDataStoreTests
    {
        private IMLOpsContext sut;

        [TestInitialize]
        public void Initialize()
        {
            var configuration = ConfigurationFactory.GetConfiguration();

            sut = new MLOpsBuilder()
                .UseModelRepository(new Mock<IModelRepository>().Object)
                .UseCosmosDb(configuration[ConfigurationKeys.CosmosEndPoint],
                configuration[ConfigurationKeys.CosmosAccountKey])
                .Build();
        }

        [TestCleanup]
        public async Task TearDown()
        {
            var configuration = ConfigurationFactory.GetConfiguration();

            var options = new DbContextOptionsBuilder()
                .UseCosmos(configuration[ConfigurationKeys.CosmosEndPoint],
                configuration[ConfigurationKeys.CosmosAccountKey], "MLOpsNET")
                .Options;

            var contextFactory = new DbContextFactory(options, CosmosEntityConfigurator.OnModelCreating);
            var context = contextFactory.CreateDbContext();

            var experiments = context.Experiments;
            var runs = context.Runs;
            var metrics = context.Metrics;
            var hyperParameters = context.HyperParameters;
            var confusionMatrices = context.ConfusionMatrices;
            var data = context.Data;

            context.Experiments.RemoveRange(experiments);
            context.Runs.RemoveRange(runs);
            context.Metrics.RemoveRange(metrics);
            context.HyperParameters.RemoveRange(hyperParameters);
            context.ConfusionMatrices.RemoveRange(confusionMatrices);
            context.Data.RemoveRange(data);

            await context.SaveChangesAsync();
        }

        [TestMethod]
        public async Task CreateExperimentAsync_ShouldCreateAnExperiment()
        {
            //Act
            var id = await sut.LifeCycle.CreateExperimentAsync("test");

            //Assert
            var experiement = sut.LifeCycle.GetExperiment("test");
            experiement.Should().NotBeNull();
            experiement.ExperimentId.Should().Be(id);
        }

        [TestMethod]
        public async Task CreateExperimentAsync_Twice_ShouldNotAddDuplicate()
        {
            //Act
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var experimentId2 = await sut.LifeCycle.CreateExperimentAsync("test");

            //Assert
            experimentId.Should().Be(experimentId2);
        }

        [TestMethod]
        public async Task CreateRunAsync_ShouldCreateRun()
        {
            //Act
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var id = await sut.LifeCycle.CreateRunAsync(experimentId);

            //Assert
            var run = sut.LifeCycle.GetRun(id);
            run.Should().NotBeNull();
            run.RunId.Should().Be(id);
        }

        [TestMethod]
        public async Task LogMetricAsync_ShouldLogMetric()
        {
            //Arrange
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var id = await sut.LifeCycle.CreateRunAsync(experimentId);

            //Act
            await sut.Evaluation.LogMetricAsync(id, "F1Score", 0.78d);

            //Assert
            var metric = sut.Evaluation.GetMetrics(id).First();
            metric.Should().NotBeNull();
            metric.MetricName.Should().Be("F1Score");
            metric.Value.Should().Be(0.78d);
        }

        [TestMethod]
        public async Task CreateRunWithMetrics_GetRunShouldIncludeAssociatedData()
        {
            //Arrange
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var id = await sut.LifeCycle.CreateRunAsync(experimentId);

            await sut.Evaluation.LogMetricAsync(id, "F1Score", 0.56d);
            await sut.Training.LogHyperParameterAsync(id, "Trainer", "SupportVectorMachine");

            //Act
            var run = sut.LifeCycle.GetRun(id);

            //Assert
            var metric = run.Metrics.First();
            metric.MetricName.Should().Be("F1Score");
            metric.Value.Should().Be(0.56d);

            var hyperParameter = run.HyperParameters.First();
            hyperParameter.ParameterName.Should().Be("Trainer");
            hyperParameter.Value.Should().Be("SupportVectorMachine");
        }

        [TestMethod]
        public async Task SetTrainingTimeAsync_ShouldTrainingTime()
        {
            //Arrange
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var id = await sut.LifeCycle.CreateRunAsync(experimentId);

            var trainingTime = new TimeSpan(0, 5, 0);

            //Act
            await sut.LifeCycle.SetTrainingTimeAsync(id, trainingTime);

            //Assert
            var run = sut.LifeCycle.GetRun(id);
            run.TrainingTime.Should().Be(trainingTime);
        }

        [TestMethod]
        public async Task GetConfusionMatrix_NoConfusionMatrixExist_ShouldReturnNull()
        {
            //Arrange
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var runId = await sut.LifeCycle.CreateRunAsync(experimentId);

            //Act
            var confusionMatrix = sut.Evaluation.GetConfusionMatrix(runId);

            //Assert
            confusionMatrix.Should().BeNull();
        }

        [TestMethod]
        public async Task LogDataAsync_GivenValidDataView_ShouldLogData()
        {
            //Arrange
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var runId = await sut.LifeCycle.CreateRunAsync(experimentId);

            var data = LoadData();

            //Act
            await sut.Data.LogDataAsync(runId, data);

            //Assert
            var savedData = sut.Data.GetData(runId);

            savedData.DataSchema.ColumnCount.Should().Be(2);

            savedData.DataSchema.DataColumns
                .Any(x => x.Type == nameof(Boolean) && x.Name == "Sentiment")
                .Should()
                .BeTrue();

            savedData.DataSchema.DataColumns
                .Any(x => x.Type == nameof(String) && x.Name == "Review")
                .Should()
                .BeTrue();
        }

        [TestMethod]
        public async Task CreateRunAsync_WithGitCommitHash_SetsGitCommitHash()
        {
            var gitCommitHash = "12323239329392";
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");

            //Act
            var runId = await sut.LifeCycle.CreateRunAsync(experimentId, gitCommitHash);

            //Assert
            var run = sut.LifeCycle.GetRun(runId);
            run.GitCommitHash.Should().Be(gitCommitHash);
        }

        [TestMethod]
        public async Task CreateRunAsync_WithoutGitCommitHash_ShouldProvideEmptyGitCommitHash()
        {
            //Act
            var experimentId = await sut.LifeCycle.CreateExperimentAsync("test");
            var runId = await sut.LifeCycle.CreateRunAsync(experimentId);

            //Assert
            var run = sut.LifeCycle.GetRun(runId);
            run.GitCommitHash.Should().Be(string.Empty);
        }

        [TestMethod]
        public async Task GivenARunWithGitCommitHash_ShouldBeAbleToGetRun()
        {
            //Arrange
            var commitHash = "123456789";
            var runId = await sut.LifeCycle.CreateRunAsync("Experiment", commitHash);

            //Act
            var savedRun = sut.LifeCycle.GetRun(commitHash);

            //Assert
            savedRun.RunId.Should().Be(runId);
        }

        private IDataView LoadData()
        {
            var mlContext = new MLContext(seed: 1);

            return mlContext.Data.LoadFromTextFile<ProductReview>("Data/product_reviews.csv", hasHeader: true, separatorChar: ',');
        }
    }
}
