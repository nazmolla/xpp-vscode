# Ask Copilot about your F&O code

F&O Studio gives Copilot (agent mode) tools for the D365FO metadata: it reads and creates elements, finds references and
standard examples, checks security, builds, runs tests and, with database access, reads business data.

Try:

- *Create a table extension of CustTable with a field for the customer's loyalty tier, and show it on the CustTable form.*
- *Why does this method fail with "Account does not exist"? Find where the message comes from.*
- *Which roles can open the SalesTable form, and what license do they need?*
- *Write SysTest tests for the class I have open and run them.*
- *How many open sales orders does company usmf have per status?* (needs database access)

Copilot asks before it changes elements of your models; standard elements are only extended, never changed.
