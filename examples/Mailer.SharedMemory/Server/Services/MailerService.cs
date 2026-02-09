#region Copyright notice and license

// Copyright 2025 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using Grpc.Core;
using Mail;

namespace Server;

public class MailerService : Mailer.MailerBase
{
    private readonly ILogger _logger;
    private readonly MailQueueRepository _messageQueueRepository;

    public MailerService(ILoggerFactory loggerFactory, MailQueueRepository messageQueueRepository)
    {
        _logger = loggerFactory.CreateLogger<MailerService>();
        _messageQueueRepository = messageQueueRepository;
    }

    public override async Task Mailbox(
        IAsyncStreamReader<ForwardMailMessage> requestStream,
        IServerStreamWriter<MailboxMessage> responseStream,
        ServerCallContext context)
    {
        var mailboxName = context.RequestHeaders.Single(e => e.Key == "mailbox-name").Value;

        var mailQueue = _messageQueueRepository.GetMailQueue(mailboxName);

        _logger.LogInformation("Connected to {MailboxName}", mailboxName);

        mailQueue.Changed += ReportChanges;

        try
        {
            while (await requestStream.MoveNext())
            {
                if (mailQueue.TryForwardMail(out var message))
                {
                    _logger.LogInformation("Forwarded mail: {Content}", message.Content);
                }
                else
                {
                    _logger.LogWarning("No mail to forward.");
                }
            }
        }
        finally
        {
            mailQueue.Changed -= ReportChanges;
        }

        _logger.LogInformation("{MailboxName} disconnected", mailboxName);

        async Task ReportChanges(MailQueueChangeState state)
        {
            await responseStream.WriteAsync(new MailboxMessage
            {
                Forwarded = state.ForwardedCount,
                New = state.TotalCount - state.ForwardedCount,
                Reason = state.Reason
            });
        }
    }
}
