using System;
using FluentAssertions;
using Halibut.ServiceModel;
using Halibut.Tests.TestServices;
using Xunit;

namespace Halibut.Tests
{
    public class FailureModesFixture
    {
        DelegateServiceFactory services;

        public FailureModesFixture()
        {
            services = new DelegateServiceFactory();
            services.Register<IEchoService>(() => new EchoService());
        }

        [Fact]
        public void FailsWhenSendingToPollingMachineButNothingPicksItUp()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            {
                var echo = octopus.CreateClient<IEchoService>("poll://SQ-TENTAPOLL", Certificates.TentaclePollingPublicThumbprint);
                var error = Assert.Throws<HalibutClientException>(() => echo.SayHello("Paul"));
                error.Message.Should().Contain("the polling endpoint did not collect the request within the allowed time");
            }
        }

        [Fact]
        public void FailWhenServerThrowsAnException()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            using (var tentacleListening = new HalibutRuntime(services, Certificates.TentacleListening))
            {
                var tentaclePort = tentacleListening.Listen();
                tentacleListening.Trust(Certificates.OctopusPublicThumbprint);

                var echo = octopus.CreateClient<IEchoService>("https://localhost:" + tentaclePort, Certificates.TentacleListeningPublicThumbprint);
                var ex = Assert.Throws<HalibutClientException>(() => echo.Crash());
                ex.Message.Should().Contain("at Halibut.Tests.TestServices.EchoService.Crash()").And.Contain("divide by zero");
            }
        }

        [Fact]
        public void FailWhenServerThrowsAnExceptionOnPolling()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            using (var tentaclePolling = new HalibutRuntime(services, Certificates.TentaclePolling))
            {
                var octopusPort = octopus.Listen();
                octopus.Trust(Certificates.TentaclePollingPublicThumbprint);

                tentaclePolling.Poll(new Uri("poll://SQ-TENTAPOLL"), new ServiceEndPoint(new Uri("https://localhost:" + octopusPort), Certificates.OctopusPublicThumbprint));

                var echo = octopus.CreateClient<IEchoService>("poll://SQ-TENTAPOLL", Certificates.TentaclePollingPublicThumbprint);
                var ex = Assert.Throws<HalibutClientException>(() => echo.Crash());
                ex.Message.Should().Contain("at Halibut.Tests.TestServices.EchoService.Crash()").And.Contain("divide by zero");
            }
        }

        [Fact]
        public void FailOnInvalidHostname()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            {
                var echo = octopus.CreateClient<IEchoService>("https://sduj08ud9382ujd98dw9fh934hdj2389u982:8000", Certificates.TentacleListeningPublicThumbprint);
                var ex = Assert.Throws<HalibutClientException>(() => echo.Crash());
                ex.Message.Should().Contain("when sending a request to 'https://sduj08ud9382ujd98dw9fh934hdj2389u982:8000/', before the request").And.Contain("No such host is known");
            }
        }

        [Fact]
        public void FailOnInvalidPort()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            {
                var echo = octopus.CreateClient<IEchoService>("https://google.com:88", Certificates.TentacleListeningPublicThumbprint);
                var ex = Assert.Throws<HalibutClientException>(() => echo.Crash());
                ex.Message.Should().Contain("when sending a request to 'https://google.com:88/', before the request");
            }
        }

        [Fact]
        public void FailWhenListeningClientPresentsWrongCertificate()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.TentaclePolling))
            using (var tentacleListening = new HalibutRuntime(services, Certificates.TentacleListening))
            {
                var tentaclePort = tentacleListening.Listen();
                tentacleListening.Trust(Certificates.OctopusPublicThumbprint);

                var echo = octopus.CreateClient<IEchoService>("https://localhost:" + tentaclePort, Certificates.TentacleListeningPublicThumbprint);

                Assert.Throws<HalibutClientException>(() => echo.SayHello("World"));
            }
        }

        [Fact]
        public void FailWhenListeningServerPresentsWrongCertificate()
        {
            using (var octopus = new HalibutRuntime(services, Certificates.Octopus))
            using (var tentacleListening = new HalibutRuntime(services, Certificates.TentacleListening))
            {
                var tentaclePort = tentacleListening.Listen();
                tentacleListening.Trust(Certificates.OctopusPublicThumbprint);

                var echo = octopus.CreateClient<IEchoService>("https://localhost:" + tentaclePort, Certificates.TentaclePollingPublicThumbprint);

                var ex = Assert.Throws<HalibutClientException>(() => echo.SayHello("World"));
            }
        }
    }
}