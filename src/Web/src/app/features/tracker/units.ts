import { ActivityLevel, Sex, TrackerStatsDto } from '../../core/models';

/** 1 kg expressed in pounds. */
export const LB_PER_KG = 2.20462;

// ── weight: kg <-> lb ───────────────────────────────────────────────────────

/** Kilograms → pounds. */
export function kgToLb(kg: number): number {
  return kg * LB_PER_KG;
}

/** Pounds → kilograms. */
export function lbToKg(lb: number): number {
  return lb / LB_PER_KG;
}

// ── height: cm <-> ft + in ────────────────────────────────────────────────────

/** Centimetres → whole feet + remaining inches (inches rounded to nearest, carrying to feet at 12). */
export function cmToFtIn(cm: number): { ft: number; in: number } {
  const totalIn = cm / 2.54;
  let ft = Math.floor(totalIn / 12);
  let inches = Math.round(totalIn - ft * 12);
  if (inches === 12) { ft += 1; inches = 0; }
  return { ft, in: inches };
}

/** Feet + inches → centimetres. */
export function ftInToCm(ft: number, inches: number): number {
  return (ft * 12 + inches) * 2.54;
}

// ── display formatting ────────────────────────────────────────────────────────

/** Format a metric weight (kg) for display in the chosen unit system, with the unit suffix. */
export function formatWeight(kg: number | null | undefined, imperial: boolean, dp = 1): string | null {
  if (kg == null) return null;
  if (imperial) return `${kgToLb(kg).toFixed(dp)} lb`;
  return `${kg.toFixed(dp)} kg`;
}

/** The weight unit label for the chosen system. */
export function weightUnit(imperial: boolean): string {
  return imperial ? 'lb' : 'kg';
}

// ── live stats preview (mirror of the backend TrackerStats.Compute formulas) ──

const ACTIVITY_FACTOR: Record<ActivityLevel, number> = {
  Sedentary: 1.2,
  Light: 1.375,
  Moderate: 1.55,
  Active: 1.725,
  VeryActive: 1.9,
};

/** Whole years from a `yyyy-MM-dd` DOB to `today` (birthday-aware); null if missing/future. */
export function ageFrom(dob: string | null | undefined, today: Date): number | null {
  if (!dob) return null;
  const d = new Date(dob + 'T00:00:00');
  if (isNaN(d.getTime()) || d.getTime() > today.getTime()) return null;
  let age = today.getFullYear() - d.getFullYear();
  const m = today.getMonth() - d.getMonth();
  if (m < 0 || (m === 0 && today.getDate() < d.getDate())) age--;
  return age < 0 ? null : age;
}

/** BMI category for a BMI value (mirrors the backend thresholds). */
export function bmiCategory(bmi: number): string {
  if (bmi < 18.5) return 'Underweight';
  if (bmi < 25) return 'Normal';
  if (bmi < 30) return 'Overweight';
  return 'Obese';
}

/** Half-to-even rounding to match .NET Math.Round (banker's rounding) used by the backend. */
function roundHalfEven(x: number): number {
  const floor = Math.floor(x);
  const diff = x - floor;
  if (diff > 0.5) return floor + 1;
  if (diff < 0.5) return floor;
  return floor % 2 === 0 ? floor : floor + 1;
}

/** Inputs to the live preview — metric, as the backend stores them. */
export interface StatsInputs {
  weightKg: number | null;
  heightCm: number | null;
  age: number | null;
  sex: Sex;
  activityLevel: ActivityLevel;
  goal: string;
  dailyCalorieGoal: number | null;
}

/**
 * Pure client mirror of the backend stats helper for the LIVE dialog preview. Any field whose inputs
 * are missing stays null (partial stats are fine). The dashboard panel reads day.stats from the server;
 * this exists only so the profile dialog can preview as the user types.
 */
export function computeStats(i: StatsInputs): TrackerStatsDto {
  const out: TrackerStatsDto = {
    age: i.age ?? null,
    bmi: null, bmiCategory: null, bmr: null, tdee: null,
    suggestedCalorieGoal: null, suggestedProteinG: null, suggestedCarbG: null, suggestedFatG: null,
  };

  const w = i.weightKg, h = i.heightCm;

  // BMI (weight + height).
  if (w != null && w > 0 && h != null && h > 0) {
    const m = h / 100;
    const bmi = roundHalfEven((w / (m * m)) * 10) / 10;
    out.bmi = bmi;
    out.bmiCategory = bmiCategory(bmi);
  }

  // BMR (weight + height + age + sex != Unspecified).
  let bmr: number | null = null;
  if (w != null && w > 0 && h != null && h > 0 && i.age != null && i.sex !== 'Unspecified') {
    const base = 10 * w + 6.25 * h - 5 * i.age;
    bmr = roundHalfEven(i.sex === 'Male' ? base + 5 : base - 161);
    out.bmr = bmr;
  }

  // TDEE (BMR * activity factor).
  let tdee: number | null = null;
  if (bmr != null) {
    tdee = roundHalfEven(bmr * ACTIVITY_FACTOR[i.activityLevel]);
    out.tdee = tdee;
  }

  // Suggested calorie goal from TDEE + goal.
  let suggested: number | null = null;
  if (tdee != null) {
    switch (i.goal) {
      case 'LoseWeight': suggested = tdee - 500; break;
      case 'GainMuscle': suggested = tdee + 300; break;
      default: suggested = tdee; break; // Maintain, Endurance
    }
    out.suggestedCalorieGoal = suggested;
  }

  // Suggested macros (weight + a calorie target: suggested else current daily goal).
  const calTarget = suggested ?? i.dailyCalorieGoal ?? null;
  if (w != null && w > 0 && calTarget != null && calTarget > 0) {
    const protein = roundHalfEven(1.8 * w);
    const fat = roundHalfEven(0.8 * w);
    const carbs = Math.max(0, roundHalfEven((calTarget - protein * 4 - fat * 9) / 4));
    out.suggestedProteinG = protein;
    out.suggestedFatG = fat;
    out.suggestedCarbG = carbs;
  }

  return out;
}
