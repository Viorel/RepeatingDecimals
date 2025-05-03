using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RepeatingDecimalsLibrary;

namespace RepeatingDecimals
{
    /// <summary>
    /// Interaction logic for UCRepeatingDecimals.xaml
    /// </summary>
    public partial class UCRepeatingDecimals : UserControl
    {
        const int MAX_OUTPUT_DIGITS_DECIMAL = 250000; // (for example, the period of 6918696/2996677 has 241665 digits)
        const int MAX_OUTPUT_DIGITS_FRACTION = 200;
        readonly TimeSpan DELAY_BEFORE_CALCULATION = TimeSpan.FromMilliseconds( 444 );
        readonly TimeSpan DELAY_BEFORE_PROGRESS = TimeSpan.FromMilliseconds( 455 ); // (must be greater than 'DELAY_BEFORE_CALCULATION')
        readonly TimeSpan MIN_DURATION_PROGRESS = TimeSpan.FromMilliseconds( 444 );

        bool mLoaded = false;
        bool mIsRestoreError = false;
        readonly DispatcherTimer mCalculationTimer;
        Thread? mCalculationThread = null;
        SimpleCancellable? mLastCancellable = null;
        readonly DispatcherTimer mProgressTimer = new( );
        DateTime mProgressShownTime = DateTime.MinValue;
        enum ProgressStatusEnum { None, DelayToShow, DelayToHide };
        ProgressStatusEnum mProgressStatus = ProgressStatusEnum.None;

        public UCRepeatingDecimals( )
        {
            InitializeComponent( );

            labelPleaseWait.Visibility = Visibility.Hidden;

            mCalculationTimer = new DispatcherTimer
            {
                Interval = DELAY_BEFORE_CALCULATION,
            };
            mCalculationTimer.Tick += CalculationTimer_Tick;

            mProgressTimer.Tick += ProgressTimer_Tick;
        }

        private void UserControl_Loaded( object sender, RoutedEventArgs e )
        {
            // it seems to be called multiple times

            if( !mLoaded )
            {
                mLoaded = true;

                ApplySavedData( );
            }
        }

        void ApplySavedData( )
        {
            try
            {
                textBoxInput.Text = Properties.Settings.Default.LastInput;

                textBoxInput.Focus( );
                textBoxInput.SelectAll( );
            }
            catch( Exception exc )
            {
                mIsRestoreError = true;

                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore
            }
        }

        internal void SaveData( )
        {
            if( !mIsRestoreError ) // avoid overwriting details in case of errors
            {
                Properties.Settings.Default.LastInput = textBoxInput.Text;

                Properties.Settings.Default.Save( );
            }
        }

        private void textBoxInput_TextChanged( object sender, TextChangedEventArgs e )
        {
            if( !mLoaded ) return;

            RestartCalculationTimer( );
        }

        private void textBoxInput_SelectionChanged( object sender, RoutedEventArgs e )
        {
            if( !mLoaded ) return;

            PostponeCalculationTimer( );
        }
        private void CalculationTimer_Tick( object? sender, EventArgs e )
        {
            mCalculationTimer.Stop( );

            RestartCalculations( );
        }

        void RestartCalculationTimer( )
        {
            mCalculationTimer.Stop( );
            mCalculationTimer.Start( );
            ShowProgress( );
        }

        void PostponeCalculationTimer( )
        {
            if( mCalculationTimer.IsEnabled ) RestartCalculationTimer( );
        }

        internal void Stop( )
        {
            mCalculationTimer.Stop( );
            StopThread( );
        }

        void StopThread( )
        {
            try
            {
                if( mCalculationThread != null )
                {
                    mLastCancellable?.SetCancel( );
                    mCalculationThread.Interrupt( );
                    mCalculationThread.Join( 99 );
                    mCalculationThread = null;
                }
            }
            catch( Exception exc )
            {
                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore?
            }
        }

        void RestartCalculations( )
        {
            try
            {
                StopThread( );

                Input? input = GetInput( );
                if( input == null ) return;

                mLastCancellable = new SimpleCancellable( );
                mCalculationThread = new Thread( ( ) =>
                {
                    CalculationThreadProc( mLastCancellable, input );
                } )
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };

                mCalculationThread.Start( );
            }
            catch( Exception exc )
            {
                //if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        class Input
        {
            // one of:
            public Fraction? mFraction;
            // or
            public IReadOnlyList<BigInteger>? mContinuedFractionItems;
            public bool mIsContinuedFractionNegative;
        }

        Input? GetInput( )
        {
            string input_text = textBoxInput.Text;

            if( string.IsNullOrWhiteSpace( input_text ) )
            {
                ShowOneRichTextBox( richTextBoxNote );
                HideProgress( );

                return null;
            }

            // TODO: trim insignificant zeroes (in Regex)

            Match m = RegexToParseInput( ).Match( input_text );

            if( m.Groups["integer"].Success )
            {
                // decimal

                bool is_negative = m.Groups["negative"].Success;
                bool is_exponent_negative = m.Groups["negative_exponent"].Success;
                Group floating_group = m.Groups["floating"];
                Group repeating_group = m.Groups["repeating"];
                Group exponent_group = m.Groups["exponent"];

                BigInteger integer = BigInteger.Parse( m.Groups["integer"].Value, CultureInfo.InvariantCulture );
                BigInteger exponent = exponent_group.Success ? BigInteger.Parse( exponent_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                if( is_exponent_negative ) exponent = -exponent;

                if( floating_group.Success || repeating_group.Success )
                {
                    // 123.45, 123.45(67), 123.(67), maybe with e

                    BigInteger floating = floating_group.Success ? BigInteger.Parse( floating_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                    int floating_length = floating_group.Success ? floating_group.Value.Length : 0;
                    BigInteger floating_magnitude = BigInteger.Pow( 10, floating_length );

                    if( repeating_group.Success )
                    {
                        // 123.45(67), 123.(67), maybe with e

                        BigInteger repeating = BigInteger.Parse( repeating_group.Value, CultureInfo.InvariantCulture );
                        int repeating_length = repeating_group.Value.Length;

                        BigInteger repeating_magnitude = BigInteger.Pow( 10, repeating_length );

                        BigInteger significant = integer * floating_magnitude + floating;
                        BigInteger significant_with_repeating = significant * repeating_magnitude + repeating;
                        Debug.Assert( significant_with_repeating >= significant );
                        BigInteger numerator = significant_with_repeating - significant;
                        BigInteger denominator = floating_magnitude * ( repeating_magnitude - 1 );

                        Fraction fraction = new( is_negative ? -numerator : numerator, denominator, exponent );

                        return new Input { mFraction = fraction };
                    }
                    else
                    {
                        // 123.45, maybe with e

                        BigInteger significant = integer * floating_magnitude + floating;
                        BigInteger adjusted_exponent = exponent - floating_length;

                        Fraction fraction = new( is_negative ? -significant : significant, BigInteger.One, adjusted_exponent );

                        return new Input { mFraction = fraction };
                    }
                }
                else
                {
                    // 123, 123e45

                    Fraction fraction = new( is_negative ? -integer : integer, BigInteger.One, exponent );

                    return new Input { mFraction = fraction };
                }
            }

            if( m.Groups["numerator"].Success )
            {
                // rational

                bool is_negative = m.Groups["negative"].Success;
                bool is_exponent_negative = m.Groups["negative_exponent"].Success;
                Group denominator_group = m.Groups["denominator"];
                Group exponent_group = m.Groups["exponent"];

                BigInteger numerator = BigInteger.Parse( m.Groups["numerator"].Value, CultureInfo.InvariantCulture );
                BigInteger denominator = denominator_group.Success ? BigInteger.Parse( denominator_group.Value, CultureInfo.InvariantCulture ) : BigInteger.One;
                BigInteger exponent = exponent_group.Success ? BigInteger.Parse( exponent_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                if( is_exponent_negative ) exponent = -exponent;

                Fraction fraction;

                if( numerator.IsZero )
                {
                    if( denominator.IsZero )
                    {
                        fraction = Fraction.Undefined;
                    }
                    else
                    {
                        fraction = Fraction.Zero;
                    }
                }
                else
                {
                    if( denominator.IsZero )
                    {
                        fraction = is_negative ? Fraction.NegativeInfinity : Fraction.PositiveInfinity;
                    }
                    else
                    {
                        fraction = new Fraction( is_negative ? -numerator : numerator, denominator, exponent );
                    }
                }

                return new Input { mFraction = fraction };
            }

            if( m.Groups["first"].Success )
            {
                // continued fraction

                bool is_negative = m.Groups["negative"].Success;
                BigInteger first = BigInteger.Parse( m.Groups["first"].Value );

                List<BigInteger> list = [first];

                Group next_group = m.Groups["next"];

                if( next_group.Success )
                {
                    foreach( Capture c in next_group.Captures )
                    {
                        BigInteger item = BigInteger.Parse( c.Value );

                        list.Add( item );
                    }
                }

                return new Input { mContinuedFractionItems = list, mIsContinuedFractionNegative = is_negative };
            }

            if( m.Groups["pi"].Success )
            {
                return new Input { mFraction = Fraction.Pi };
            }

            if( m.Groups["e"].Success )
            {
                return new Input { mFraction = Fraction.EulerNumber };
            }

            ShowOneRichTextBox( richTextBoxTypicalError );
            HideProgress( );

            return null;
        }


        void CalculationThreadProc( ICancellable cnc, Input input )
        {
            try
            {
                Fraction fraction;

                if( input.mFraction != null )
                {
                    fraction = input.mFraction;
                }
                else if( input.mContinuedFractionItems != null )
                {
                    var p =
                        ContinuedFractionUtilities
                            .EnumerateContinuedFractionConvergents( input.mContinuedFractionItems )
                            .Last( );

                    fraction = p.d.IsZero ? p.n < 0 ? Fraction.NegativeInfinity : p.n > 0 ? Fraction.PositiveInfinity : Fraction.Undefined
                               : new Fraction( p.d < 0 ? -p.n : p.n, BigInteger.Abs( p.d ) );

                    CalculationContext ctx = new( cnc, 33 );

                    if( input.mIsContinuedFractionNegative ) fraction = Fraction.Neg( fraction, ctx );
                }
                else
                {
                    throw new InvalidOperationException( );
                }

                if( !fraction.IsNormal )
                {
                    ShowResults( cnc, fraction );
                    HideProgress( );
                }
                else
                {
                    CalculationContext ctx = new( cnc, 33 );

                    fraction = fraction.Simplify( ctx );

                    ShowResults( cnc, fraction );
                    HideProgress( );
                }
            }
            catch( OperationCanceledException ) // also 'TaskCanceledException'
            {
                // (the operation is supposed to be restarted)
                return;
            }
            catch( Exception exc )
            {
                //if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        void ShowResults( ICancellable cnc, Fraction initialFraction )
        {
            string as_decimal = initialFraction.ToFloatString( cnc, MAX_OUTPUT_DIGITS_DECIMAL );

            bool is_decimal_approx = as_decimal.StartsWith( '≈' );
            int period = 0;
            {
                int left_par = as_decimal.IndexOf( '(' );
                int right_par = as_decimal.IndexOf( ')' );
                if( left_par > 0 && right_par > left_par )
                {
                    period = right_par - left_par - 1;
                }
            }
            bool is_repeating = as_decimal.Contains( '(' );

            string as_fraction = initialFraction.ToRationalString( cnc, MAX_OUTPUT_DIGITS_FRACTION );
            as_fraction = Regex.Replace( as_fraction, @"\s*/\s*", " / " );
            //as_decimal = as_decimal.Replace( ".", ".\u2060" ); // (do not break after '.')

            Dispatcher.BeginInvoke( ( ) =>
            {
                runDecimal.Text = as_decimal;
                runFraction.Text = as_fraction;

                if( is_decimal_approx )
                {
                    runNote.Text = "⚠️ The period is too long.";
                }
                else
                {
                    switch( period )
                    {
                    case 0:
                        runNote.Text = "Not a repeating decimal.";
                        break;
                    case > 0:
                        runNote.Text = $"The period is {period}.";
                        break;
                    default:
                        runNote.Text = ""; //
                        break;
                    }
                }

                {
                    // adjust page width to avoid wrapping

                    string text = new TextRange( richTextBoxResults.Document.ContentStart, richTextBoxResults.Document.ContentEnd ).Text;
                    FormattedText ft = new( text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface( richTextBoxResults.FontFamily, richTextBoxResults.FontStyle, richTextBoxResults.FontWeight, richTextBoxResults.FontStretch ), richTextBoxResults.FontSize, Brushes.Black, VisualTreeHelper.GetDpi( richTextBoxResults ).PixelsPerDip );

                    richTextBoxResults.Document.PageWidth = ft.Width + 100;
                }

                ShowOneRichTextBox( richTextBoxResults );
            } );
        }

        void ShowError( string errorText )
        {
            if( !Dispatcher.CheckAccess( ) )
            {
                Dispatcher.BeginInvoke( ( ) =>
                {
                    ShowError( errorText );
                } );
            }
            else
            {
                runError.Text = errorText;
                ShowOneRichTextBox( richTextBoxError );
                HideProgress( );
            }
        }

        void ShowOneRichTextBox( RichTextBox richTextBox )
        {
            bool was_visible = richTextBox.Visibility == Visibility.Visible;

            richTextBoxNote.Visibility = Visibility.Hidden;
            richTextBoxTypicalError.Visibility = Visibility.Hidden;
            richTextBoxError.Visibility = Visibility.Hidden;
            richTextBoxResults.Visibility = Visibility.Hidden;

            if( !was_visible ) richTextBox.ScrollToHome( );
            richTextBox.Visibility = Visibility.Visible;
        }

        #region Progress indicator

        void ShowProgress( )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( mProgressShownTime != DateTime.MinValue )
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif
                return;
            }
            else
            {
                mProgressStatus = ProgressStatusEnum.DelayToShow;
                mProgressTimer.Interval = DELAY_BEFORE_PROGRESS;
                mProgressTimer.Start( );
            }
        }

        void HideProgress( bool rightNow = false )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( rightNow || mProgressShownTime == DateTime.MinValue )
            {
                Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                mProgressShownTime = DateTime.MinValue;
            }
            else
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif

                TimeSpan elapsed = DateTime.Now - mProgressShownTime;

                if( elapsed >= MIN_DURATION_PROGRESS )
                {
                    Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                    mProgressShownTime = DateTime.MinValue;
                }
                else
                {
                    mProgressStatus = ProgressStatusEnum.DelayToHide;
                    mProgressTimer.Interval = MIN_DURATION_PROGRESS - elapsed;
                    mProgressTimer.Start( );
                }
            }
        }

        private void ProgressTimer_Tick( object? sender, EventArgs e )
        {
            mProgressTimer.Stop( );

            switch( mProgressStatus )
            {
            case ProgressStatusEnum.DelayToShow:
                labelPleaseWait.Visibility = Visibility.Visible;
                mProgressShownTime = DateTime.Now;
                break;
            case ProgressStatusEnum.DelayToHide:
                labelPleaseWait.Visibility = Visibility.Hidden;
                mProgressShownTime = DateTime.MinValue;
                break;
            case ProgressStatusEnum.None:
                //
                break;
            default:
                Debug.Assert( false );
                break;
            }

            mProgressStatus = ProgressStatusEnum.None;
        }

        #endregion

        [GeneratedRegex( """
            (?xni)^ \s* 
            (
              ( # decimal
                (\+|(?<negative>-))? \s* (?<integer>\d+) 
                ((\s* \. \s* (?<floating>\d+)) | \.)? 
                (\s* \( \s* (?<repeating>\d+) \s* \) )? 
                (\s* [eE] \s* (\+|(?<negative_exponent>-))? \s* (?<exponent>\d+))? 
              )
            |
              ( # rational
                (\+|(?<negative>-))? \s* (?<numerator>\d+) 
                (\s* [eE] \s* (\+|(?<negative_exponent>-))? \s* (?<exponent>\d+))? 
                \s* / \s*
                (?<denominator>\d+) 
              )
            |
              ( # continued fraction
                (\+|(?<negative>-))? \s*
                \[
                \s* (?<first>[\-\+]?\d+)(\s*[;,\s]\s*(?<next>[\-\+]?\d+))* \s*
                \]?
              )
            |
              (?<pi>pi | π)
            |
              (?<e>e) 
            )
            \s* $
            """, RegexOptions.IgnorePatternWhitespace
        )]
        private static partial Regex RegexToParseInput( );
    }
}
