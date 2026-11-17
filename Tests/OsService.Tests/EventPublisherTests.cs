using System.Text;
using Moq;
using RabbitMQ.Client;
using Xunit;

public sealed class EventPublisherTests
{
    [Fact]
    public async Task Publishes_Persistent_Message_And_Waits_For_Broker_Confirmation()
    {
        var properties = new Mock<IBasicProperties>();
        properties.SetupAllProperties();
        var channel = new Mock<IModel>();
        channel.Setup(x => x.CreateBasicProperties()).Returns(properties.Object);
        var connection = new Mock<IConnection>();
        connection.Setup(x => x.CreateModel()).Returns(channel.Object);
        var factory = new Mock<IConnectionFactory>();
        factory.Setup(x => x.CreateConnection()).Returns(connection.Object);

        await new EventPublisher(factory.Object).PublishAsync("OS_CREATED", "{\"type\":\"OS_CREATED\"}");

        channel.Verify(x => x.QueueBind("billing-service-events", "oficina.events", "OS_CREATED", null), Times.Once);
        channel.Verify(x => x.QueueBind("execution-service-events", "oficina.events", "OS_READY_FOR_EXECUTION", null), Times.Once);
        channel.Verify(x => x.ConfirmSelect(), Times.Once);
        channel.Verify(x => x.BasicPublish("oficina.events", "OS_CREATED", false, properties.Object, It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        channel.Verify(x => x.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5)), Times.Once);
        Assert.True(properties.Object.Persistent);
        var publish = Assert.Single(channel.Invocations, x => x.Method.Name == "BasicPublish");
        Assert.Equal("{\"type\":\"OS_CREATED\"}", Encoding.UTF8.GetString(((ReadOnlyMemory<byte>)publish.Arguments[4]!).Span));
    }
}
