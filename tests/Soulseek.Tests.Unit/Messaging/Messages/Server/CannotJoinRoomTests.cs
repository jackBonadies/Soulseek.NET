﻿// <copyright file="CannotJoinRoomTests.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace Soulseek.Tests.Unit.Messaging.Messages
{
    using AutoFixture.Xunit2;
    using Soulseek.Messaging;
    using Soulseek.Messaging.Messages;
    using Xunit;

    public class CannotJoinRoomTests
    {
        [Trait("Category", "FromByteArray")]
        [Theory(DisplayName = "FromByteArray returns the expected data"), AutoData]
        public void FromByteArray_Returns_Expected_Data(string roomName)
        {
            var msg = new MessageBuilder()
                .WriteCode(MessageCode.Server.CannotJoinRoom)
                .WriteString(roomName)
                .Build();

            var m = CannotJoinRoom.FromByteArray(msg);

            Assert.Equal(roomName, m.RoomName);
        }

        [Trait("Category", "FromByteArray")]
        [Fact(DisplayName = "FromByteArray throws MessageException on code mismatch")]
        public void FromByteArray_Throws_MessageException_On_Code_Mismatch()
        {
            var msg = new MessageBuilder()
                .WriteCode(MessageCode.Server.BranchLevel)
                .WriteInteger(1)
                .Build();

            var ex = Record.Exception(() => CannotJoinRoom.FromByteArray(msg));

            Assert.NotNull(ex);
            Assert.IsType<MessageException>(ex);
        }
    }
}
