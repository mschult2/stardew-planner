This package contains all of the business logic. The other packages just make a pretty GUI.

A dynamic programming algorithm is used which computes the max-investment-multiple for a particular day, and then caches this answer in a memo table. If computing the max-investment-multiple for another day is dependent in part on a previously computed day, that previously computed day's answer will be summoned from the memo table rather than being recomputed.

The implementation is iterative, and moves backwards through each day in the season.