namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// How many Console entries the Console window's own filter is withholding, and the sentence
    /// that says so.
    /// </summary>
    /// <remarks>
    /// <c>LogEntries.GetCount</c> answers with that window's filter applied: its Error, Warning
    /// and Log toggles and the text in its search box. <c>GetCountsByType</c> ignores both. A
    /// reply built from the first alone reports a smaller number with nothing to say why, and a
    /// search box left with text in it reports zero entries while the Console holds hundreds.
    /// <para>
    /// Both console tools reach the same Console through different code, so the arithmetic and
    /// the wording live here rather than in either of them.
    /// </para>
    /// </remarks>
    internal static class ConsoleFilterNotice
    {
        /// <summary>
        /// Entries the window is hiding, given what a filtered call reported and the unfiltered
        /// counts by severity.
        /// </summary>
        internal static int Hidden(int visible, int errors, int warnings, int logs)
        {
            var everything = errors + warnings + logs;

            return everything > visible ? everything - visible : 0;
        }

        /// <summary>The sentence a caller needs to see when entries are being withheld.</summary>
        internal static string Text(int hidden)
        {
            return $"The Console window is filtering: {hidden} more entries exist than are " +
                   "reported here. Its Error, Warning and Log toggles and its search box apply " +
                   "to this call. Turn the toggles on and clear the search box to see them all.";
        }
    }
}
