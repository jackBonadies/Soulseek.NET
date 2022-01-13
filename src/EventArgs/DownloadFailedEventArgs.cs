﻿// <copyright file="DownloadFailedEventArgs.cs" company="JP Dillingham">
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

namespace Soulseek
{
    /// <summary>
    ///     Event arguments for events raised when a user reports that an upload has failed.
    /// </summary>
    public class DownloadFailedEventArgs : UserEventArgs
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="BrowseEventArgs"/> class.
        /// </summary>
        /// <param name="username">The username associated with the event.</param>
        /// <param name="filename">The filename associated with the event.</param>
        public DownloadFailedEventArgs(string username, string filename)
            : base(username)
        {
            Filename = filename;
        }

        /// <summary>
        ///     Gets the filename associated with the event.
        /// </summary>
        public string Filename { get; }
    }
}