/**
 * Deterministic six-month bank statements and financial statements for the assessment presets,
 * so the bank cash-flow and P&L steps run end-to-end without an upload.
 * Bank CSV format: date,description,amount,balance. Financials: label,amount per line.
 */

export interface PresetFinancials {
  bankStatementCsv: string;
  financialStatementText: string;
}

interface StatementProfile {
  /** Net monthly card settlements, split across the listed processor descriptors. */
  monthlyCardDeposits: number;
  processors: string[];
  otherIncome: number;
  rent: number;
  payroll: number;
  suppliers: number;
  loanRepayment?: string;
  loanAmount?: number;
  ownerDraw: number;
  openingBalance: number;
  /** Extra events keyed by month index (0-5): description and amount. */
  events?: Record<number, Array<[string, number]>>;
}

const MONTHS = ['2025-01', '2025-02', '2025-03', '2025-04', '2025-05', '2025-06'];
const DAYS_IN_MONTH = [31, 28, 31, 30, 31, 30];

const money = (n: number) => n.toFixed(2);

/** Small deterministic wobble so months are not identical (±8%). */
const wobble = (base: number, seed: number) => Math.round(base * (1 + (((seed * 37) % 17) - 8) / 100));

function buildStatement(p: StatementProfile): string {
  const rows: string[] = ['date,description,amount,balance'];
  let balance = p.openingBalance;
  const push = (date: string, desc: string, amount: number) => {
    balance = Math.round((balance + amount) * 100) / 100;
    rows.push(`${date},${desc},${money(amount)},${money(balance)}`);
  };

  MONTHS.forEach((month, mi) => {
    const days = DAYS_IN_MONTH[mi];
    const d = (day: number) => `${month}-${String(Math.min(day, days)).padStart(2, '0')}`;
    const monthCard = wobble(p.monthlyCardDeposits, mi + 1);
    const perProcessor = monthCard / p.processors.length;

    push(d(1), 'Rent - commercial lease', -p.rent);
    push(d(2), 'Utilities - power & water', -wobble(Math.round(p.rent * 0.08), mi + 3));
    if (p.otherIncome) push(d(3), 'Wholesale invoice payment', wobble(p.otherIncome, mi + 5));

    for (let week = 0; week < 4; week++) {
      const day = 4 + week * 7;
      p.processors.forEach((proc, pi) => {
        push(d(day + pi), `${proc} settlement`, Math.round((perProcessor / 4) * 100) / 100);
      });
      push(d(day + 2), 'Supplier payment - inventory', -Math.round(p.suppliers / 4));
    }

    push(d(15), 'Payroll - Gusto', -Math.round(p.payroll / 2));
    push(d(30), 'Payroll - Gusto', -Math.round(p.payroll / 2));
    if (p.loanRepayment && p.loanAmount) push(d(20), p.loanRepayment, -p.loanAmount);
    push(d(25), 'Insurance premium', -Math.round(p.rent * 0.12));
    if (p.ownerDraw) push(d(27), "Owner's draw", -p.ownerDraw);
    for (const [desc, amount] of p.events?.[mi] ?? []) push(d(22), desc, amount);
  });
  return rows.join('\n');
}

function financials(lines: Array<[string, number]>): string {
  return lines.map(([label, amount]) => `${label},${amount}`).join('\n');
}

/** Starbucks preset: steady inflows, single processor, healthy liquidity. Declared volume $900k. */
const approved: PresetFinancials = {
  bankStatementCsv: buildStatement({
    monthlyCardDeposits: 72_800,
    processors: ['Fiserv merchant services'],
    otherIncome: 6_000,
    rent: 6_500,
    payroll: 28_000,
    suppliers: 22_000,
    ownerDraw: 4_000,
    openingBalance: 92_000
  }),
  financialStatementText: financials([
    ['Revenue', 1_000_000],
    ['Cost of goods sold', 380_000],
    ['Gross profit', 620_000],
    ['Operating expenses', 455_000],
    ['Operating income', 165_000],
    ['Interest expense', 4_000],
    ['Depreciation', 18_000],
    ['Net income', 118_000],
    ['Total assets', 640_000],
    ['Current assets', 310_000],
    ['Cash and cash equivalents', 165_000],
    ['Total liabilities', 210_000],
    ['Current liabilities', 120_000],
    ['Total debt', 60_000],
    ['Equity', 430_000]
  ])
};

/** Apple preset: strong cash flow so the only open item stays the MCC mismatch. Declared volume $1.2M. */
const clean: PresetFinancials = {
  bankStatementCsv: buildStatement({
    monthlyCardDeposits: 97_000,
    processors: ['Adyen'],
    otherIncome: 14_000,
    rent: 9_000,
    payroll: 41_000,
    suppliers: 30_000,
    ownerDraw: 0,
    openingBalance: 210_000
  }),
  financialStatementText: financials([
    ['Revenue', 1_450_000],
    ['Cost of goods sold', 610_000],
    ['Gross profit', 840_000],
    ['Operating expenses', 560_000],
    ['Operating income', 280_000],
    ['Interest expense', 2_500],
    ['Depreciation', 35_000],
    ['Net income', 205_000],
    ['Total assets', 1_250_000],
    ['Current assets', 620_000],
    ['Cash and cash equivalents', 390_000],
    ['Total liabilities', 310_000],
    ['Current liabilities', 190_000],
    ['Total debt', 45_000],
    ['Equity', 940_000]
  ])
};

/**
 * GreenLeaf preset: statements corroborate ~$1.5M, not the declared $4.8M; three processors,
 * NSF fees, chargebacks, merchant cash advance, heavy owner draws and negative balance days.
 */
const restricted: PresetFinancials = {
  bankStatementCsv: buildStatement({
    monthlyCardDeposits: 118_000,
    processors: ['Stripe', 'Square Inc', 'PayPal'],
    otherIncome: 0,
    rent: 3_200,
    payroll: 9_000,
    suppliers: 64_000,
    loanRepayment: 'Kabbage merchant cash advance repayment',
    loanAmount: 26_000,
    ownerDraw: 18_000,
    openingBalance: 4_500,
    events: {
      0: [['Chargeback reversal - Stripe', -1_840]],
      1: [['NSF fee - insufficient funds', -35], ['Chargeback reversal - Square', -2_210]],
      2: [['Overdraft fee', -35], ['Returned item - customer dispute', -960]],
      3: [['NSF fee - insufficient funds', -35], ['Chargeback reversal - PayPal', -3_120]],
      4: [['Zelle transfer to personal', -12_000], ['Chargeback reversal - Stripe', -2_675]],
      5: [['Overdraft fee', -35], ['Returned item - customer dispute', -1_430]]
    }
  }),
  financialStatementText: financials([
    ['Revenue', 1_380_000],
    ['Cost of goods sold', 790_000],
    ['Gross profit', 590_000],
    ['Operating expenses', 640_000],
    ['Operating income', -50_000],
    ['Interest expense', 96_000],
    ['Depreciation', 8_000],
    ['Net loss', -154_000],
    ['Total assets', 240_000],
    ['Current assets', 190_000],
    ['Cash and cash equivalents', 6_000],
    ['Total liabilities', 415_000],
    ['Current liabilities', 360_000],
    ['Total debt', 310_000],
    ['Equity', -175_000]
  ])
};

/**
 * SMB preset (Aljazzar Meat & Grill LLC, Louisville KY): illustrative six-month statement for a single-location
 * restaurant declaring $420k — one processor, modest rent, steady deposits, no NSF. No P&L: a Micro / Small
 * merchant is not expected to have one, so the financial-statement step is skipped as not applicable.
 */
const smb: PresetFinancials = {
  bankStatementCsv: buildStatement({
    monthlyCardDeposits: 33_500,
    processors: ['Toast Inc'],
    otherIncome: 1_800,
    rent: 3_600,
    payroll: 12_500,
    suppliers: 11_000,
    ownerDraw: 3_000,
    openingBalance: 14_000
  }),
  financialStatementText: ''
};

export const PRESET_FINANCIALS = { approved, clean, restricted, smb } as const;
