using Comgenie.AI.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Comgenie.AI.Memory.Utils
{
    internal class RelativeDateHelper
    {
        internal class TextWithAbsoluteDate
        {
            public string Text { get; set; }
            public DateTime? EarliestMentionedDate { get; set; }
            public DateTime? LatestMentionedDate { get; set; }

        }
        public static async Task<TextWithAbsoluteDate> ConvertTextWithRelativeDates(LLM llm, DateTime now, string text, LLMGenerationOptions generationOptions)
        {
            DateTime? earliestMentionedDate = null;
            DateTime? latestMentionedDate = null;

            var msg = new List<ChatMessage>() {
                new ChatSystemMessage("You are an assistent helping to extract any mentions of dates, times or moments from a text. This includes relative dates (tonight at 5, tomorrow, ..) or absolute ones (3 april 2025, 5th of June 2026, ..)"),
                new ChatUserMessage("Extract all mentioned dates, times or moments for the following text unless it's mentioned in an abstract way. If none are found, return an empty array:\r\n" + text)
            };

            var easyOptions = new List<(string textToFind, string textBefore, string dateFormat, DateTime startPeriod, DateTime endPeriod)>();
            easyOptions.Add(("tonight", "in the evening of ", "D", now.Date.AddHours(18), now.Date.AddDays(1)));

            easyOptions.Add(("this evening", "the evening of ", "D", now.Date.AddHours(18), now.Date.AddHours(24)));
            easyOptions.Add(("in the evening", "in the evening of ", "D", now.Date.AddHours(18), now.Date.AddHours(24)));
            easyOptions.Add(("the evening", "the evening of ", "D", now.Date.AddHours(18), now.Date.AddHours(24)));
            easyOptions.Add(("tomorrow evening", "on the evening of ", "D", now.Date.AddDays(1).AddHours(18), now.Date.AddDays(2)));
            easyOptions.Add(("yesterday evening", "on the evening of ", "D", now.Date.AddDays(-1).AddHours(18), now.Date));

            easyOptions.Add(("this afternoon", "on the afternoon of ", "D", now.Date.AddHours(12), now.Date.AddHours(18)));
            easyOptions.Add(("the afternoon", "on the afternoon of ", "D", now.Date.AddHours(12), now.Date.AddHours(18)));
            easyOptions.Add(("tomorrow afternoon", "on the afternoon of ", "D", now.Date.AddDays(1).AddHours(12), now.Date.AddDays(1).AddHours(18)));
            easyOptions.Add(("yesterday afternoon", "on the afternoon of ", "D", now.Date.AddDays(-1).AddHours(12), now.Date.AddDays(-1).AddHours(18)));

            easyOptions.Add(("this morning", "in the morning of ", "D", now.Date.AddHours(6), now.Date.AddHours(12)));
            easyOptions.Add(("in the morning", "in the morning of ", "D", now.Date.AddHours(6), now.Date.AddHours(12)));
            easyOptions.Add(("the morning", "in the morning of ", "D", now.Date.AddHours(6), now.Date.AddHours(12))); // Note, may also point to tomorrow morning..
            easyOptions.Add(("tomorrow morning", "in the morning of ", "D", now.Date.AddDays(1).AddHours(6), now.Date.AddDays(1).AddHours(12)));
            easyOptions.Add(("yesterday morning", "in the morning of ", "D", now.Date.AddDays(-1).AddHours(6), now.Date.AddDays(-1).AddHours(12)));

            easyOptions.Add(("today", "on ", "D", now.Date, now.Date.AddDays(1)));
            easyOptions.Add(("the weekday", "on ", "D", now.Date, now.Date.AddDays(1)));
            easyOptions.Add(("the current weekday", "on ", "D", now.Date, now.Date.AddDays(1)));

            easyOptions.Add(("tomorrow", "on ", "D", now.Date.AddDays(1), now.Date.AddDays(2)));
            easyOptions.Add(("yesterday", "on ", "D", now.Date.AddDays(-1), now.Date));
            easyOptions.Add(("one day ago", "on ", "D", now.Date.AddDays(-1), now.Date));
            easyOptions.Add(("the day after tomorrow", "on ", "D", now.Date.AddDays(2), now.Date.AddDays(3)));
            easyOptions.Add(("day after tomorrow", "on ", "D", now.Date.AddDays(2), now.Date.AddDays(3)));

            // Weekdays

            for (var i = 0; i < 7; i++)
            {
                var lastWeekday = now.Date.AddDays(-1);
                while ((int)lastWeekday.Date.DayOfWeek != i)
                    lastWeekday = lastWeekday.AddDays(-1);

                var nextWeekday = now.Date.AddDays(1);
                while ((int)nextWeekday.Date.DayOfWeek != i)
                    nextWeekday = nextWeekday.AddDays(1);

                var weekdayTxt = ((DayOfWeek)i).ToString();

                easyOptions.Add(("last " + weekdayTxt, "on ", "dd MMMM yyyy", lastWeekday, lastWeekday.AddDays(1)));
                easyOptions.Add(("previous " + weekdayTxt, "on ", "dd MMMM yyyy", lastWeekday, lastWeekday.AddDays(1)));

                easyOptions.Add(("next " + weekdayTxt, "on ", "dd MMMM yyyy", nextWeekday, nextWeekday.AddDays(1)));
                easyOptions.Add(("on " + weekdayTxt, "on ", "dd MMMM yyyy", nextWeekday, nextWeekday.AddDays(1)));
                easyOptions.Add(("this " + weekdayTxt, "on ", "dd MMMM yyyy", nextWeekday, nextWeekday.AddDays(1)));
                easyOptions.Add((weekdayTxt, "on ", "dd MMMM yyyy", nextWeekday, nextWeekday.AddDays(1)));
            }

            var curMonday = now.Date.AddDays(-(int)now.Date.DayOfWeek).AddDays(1);
            easyOptions.Add(("last weekend", "in the weekend of ", "dd MMMM yyyy", curMonday.AddDays(-2), curMonday));
            easyOptions.Add(("next weekend", "in the weekend of ", "dd MMMM yyyy", curMonday.AddDays(5), curMonday.AddDays(7)));
            easyOptions.Add(("this weekend", "in the weekend of ", "dd MMMM yyyy", curMonday.AddDays(5), curMonday.AddDays(7)));
            easyOptions.Add(("the weekend", "the weekend of ", "dd MMMM yyyy", curMonday.AddDays(5), curMonday.AddDays(7)));
            easyOptions.Add(("weekend", "the weekend of ", "dd MMMM yyyy", curMonday.AddDays(5), curMonday.AddDays(7)));

            easyOptions.Add(("this week", "in the week of ", "dd MMMM yyyy", curMonday, curMonday.AddDays(7)));
            easyOptions.Add(("next week", "in the week of ", "dd MMMM yyyy", curMonday.AddDays(7), curMonday.AddDays(14)));
            easyOptions.Add(("over one week", "in the week of ", "dd MMMM yyyy", curMonday.AddDays(7), curMonday.AddDays(14)));
            easyOptions.Add(("over 1 week", "in the week of ", "dd MMMM yyyy", curMonday.AddDays(7), curMonday.AddDays(14)));
            easyOptions.Add(("over 2 weeks", "in the week of ", "dd MMMM yyyy", curMonday.AddDays(14), curMonday.AddDays(21)));
            easyOptions.Add(("over 3 weeks", "in the week of ", "dd MMMM yyyy", curMonday.AddDays(21), curMonday.AddDays(28)));


            var curMonthStart = new DateTime(now.Year, now.Month, 1);
            easyOptions.Add(("this month", "", "MMMM yyyy", curMonthStart, curMonthStart.AddMonths(1)));
            easyOptions.Add(("next month", "", "MMMM yyyy", curMonthStart.AddMonths(1), curMonthStart.AddMonths(2)));
            easyOptions.Add(("last month", "", "MMMM yyyy", curMonthStart.AddMonths(-1), curMonthStart));

            var curYearStart = new DateTime(now.Year, 1, 1);
            easyOptions.Add(("this year", "in ", "yyyy", curYearStart, curYearStart.AddYears(1)));
            easyOptions.Add(("next year", "in ", "yyyy", curYearStart.AddYears(1), curYearStart.AddYears(2)));
            easyOptions.Add(("last year", "in ", "yyyy", curYearStart.AddYears(-1), curYearStart));

            easyOptions.Add(("over a year", "in ", "MMMM yyyy", now.Date.AddYears(1), now.Date.AddYears(1).AddMonths(1)));
            easyOptions.Add(("over one year", "in ", "MMMM yyyy", now.Date.AddYears(1), now.Date.AddYears(1).AddMonths(1)));
            easyOptions.Add(("in a year", "in ", "MMMM yyyy", now.Date.AddYears(1), now.Date.AddYears(1).AddMonths(1)));
            easyOptions.Add(("in one year", "in ", "MMMM yyyy", now.Date.AddYears(1), now.Date.AddYears(1).AddMonths(1)));

            List<MomentMentioned>? dateMentions;
            try
            {
                dateMentions = await llm.GenerateStructuredResponseAsync<List<MomentMentioned>>(msg, generationOptions: generationOptions);
                if (dateMentions == null)
                    dateMentions = new List<MomentMentioned>();
            }
            catch (Exception)
            {
                dateMentions = new List<MomentMentioned>();
            }

            foreach (var dateMentioned in dateMentions)
            {
                if (dateMentioned.Type == "Absolute")
                {
                    if (DateTime.TryParse(dateMentioned.Text, out DateTime date))
                    {
                        if (earliestMentionedDate == null || earliestMentionedDate.Value > date)
                            earliestMentionedDate = date;
                        if (latestMentionedDate == null || latestMentionedDate.Value < date)
                            latestMentionedDate = date;

                    }
                    continue; // Should already be fine

                }

                // Try to see if we can convert it easily
                var found = false;
                foreach (var easyOption in easyOptions)
                {
                    if (dateMentioned.Text.Equals(easyOption.textToFind, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Replace(easyOption.textToFind, easyOption.textBefore + easyOption.startPeriod.ToString(easyOption.dateFormat), StringComparison.OrdinalIgnoreCase);

                        if (earliestMentionedDate == null || earliestMentionedDate.Value > easyOption.startPeriod)
                            earliestMentionedDate = easyOption.startPeriod;
                        if (latestMentionedDate == null || latestMentionedDate.Value < easyOption.endPeriod)
                            latestMentionedDate = easyOption.endPeriod;

                        found = true;
                        break;
                    }
                }

                // Otherwise, we might want the llm to generate a script
                if (!found)
                {
                    Console.WriteLine("Could not replace " + dateMentioned.Text);
                }

            }

            // Fix replace mistakes
            text = text.Replace(" the the ", " the ");
            text = text.Replace(" the on ", " on ");
            text = text.Replace(" the in ", " in ");

            return new TextWithAbsoluteDate() { Text = text, EarliestMentionedDate = earliestMentionedDate, LatestMentionedDate = latestMentionedDate };
        }
        private class MomentMentioned
        {
            [Instruction("Exact appearance within the text")]
            public string Text { get; set; }

            [Instruction("Relative if this is a relative date (tomorrow, tuesday), Absolute if this is a full date with year. Choose Relative/Absolute")]
            public string Type { get; set; }
        }
    }
}
