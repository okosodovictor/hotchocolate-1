﻿using System;
using System.Threading.Tasks;
using HotChocolate.Subscriptions;
using HotChocolate.StarWars.Data;
using HotChocolate.StarWars.Models;

namespace HotChocolate.StarWars
{
    public class Mutation
    {
        private readonly ReviewRepository _repository;

        public Mutation(ReviewRepository repository)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Creates a review for a given Star Wars episode.
        /// </summary>
        /// <param name="episode">The episode to review.</param>
        /// <param name="review">The review.</param>
        /// <param name="eventSender">The event sending service.</param>
        /// <returns>The created review.</returns>
        public async Task<Review> CreateReview(
            Episode episode,
            Review review,
            [Service]IEventDispatcher eventDispatcher)
        {
            _repository.AddReview(episode, review);
            await eventSender.SendAsync(new OnReviewMessage(episode, review));
            return review;
        }
    }
}
