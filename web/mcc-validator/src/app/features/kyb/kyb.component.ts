import { Component, inject, signal } from '@angular/core';
import { DecimalPipe, PercentPipe } from '@angular/common';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SuiteApiService, describeError } from '../../shared/suite-api.service';
import { FullKybRequest, KybReport, RegistryRecord } from '../../shared/models';
import { FieldHintComponent, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, outcomeClass, tierClass } from '../../shared/ui';

@Component({
  selector: 'mi-kyb',
  standalone: true,
  imports: [ReactiveFormsModule, DecimalPipe, PercentPipe, MatButtonModule, MatCardModule, MatChipsModule, MatExpansionModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatTableModule, MatTabsModule, MatTooltipModule, FlagsComponent, GaugeComponent, JsonViewComponent, StatusComponent, FieldHintComponent],
  templateUrl: './kyb.component.html',
  styleUrl: '../platform/platform.scss'
})
export class KybComponent {
  private readonly api = inject(SuiteApiService);
  private readonly fb = inject(FormBuilder);
  readonly tierClass = tierClass;
  readonly outcomeClass = outcomeClass;
  readonly checkColumns = ['status', 'title', 'detail'];

  readonly form = this.fb.nonNullable.group({
    legalName: ['Apple Inc.', Validators.required],
    tradingName: [''],
    registrationNumber: [''],
    taxId: [''],
    addressLine: ['One Apple Park Way'],
    city: ['Cupertino'],
    region: ['CA'],
    postalCode: ['95014'],
    country: ['US'],
    websiteUrl: ['https://www.apple.com'],
    businessDescription: ['Consumer electronics, software and online services.'],
    declaredMcc: ['5732'],
    owners: this.fb.array([this.owner('Tim Cook', 'CEO')])
  });

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<KybReport | null>(null);

  get owners(): FormArray { return this.form.controls.owners; }
  private owner(fullName = '', role = '') { return this.fb.nonNullable.group({ fullName: [fullName], dateOfBirth: [''], nationality: [''], role: [role], ownershipPercent: [''] }); }
  addOwner(): void { this.owners.push(this.owner()); }
  removeOwner(i: number): void { this.owners.removeAt(i); }

  submit(): void {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    const opt = (s: string) => s || undefined;
    const req: FullKybRequest = {
      business: {
        legalName: v.legalName, tradingName: opt(v.tradingName), registrationNumber: opt(v.registrationNumber), taxId: opt(v.taxId),
        addressLine: opt(v.addressLine), city: opt(v.city), region: opt(v.region), postalCode: opt(v.postalCode), country: opt(v.country), websiteUrl: opt(v.websiteUrl)
      },
      owners: v.owners.filter(o => o.fullName.trim()).map(o => ({
        fullName: o.fullName, dateOfBirth: opt(o.dateOfBirth), nationality: opt(o.nationality), role: opt(o.role),
        ownershipPercent: o.ownershipPercent === '' ? undefined : Number(o.ownershipPercent)
      })),
      businessDescription: opt(v.businessDescription),
      declaredMcc: v.declaredMcc === '' ? undefined : Number(v.declaredMcc)
    };
    this.loading.set(true); this.error.set(null); this.report.set(null);
    this.api.kybReport(req).subscribe({
      next: r => { this.report.set(r); this.loading.set(false); },
      error: e => { this.error.set(describeError(e)); this.loading.set(false); }
    });
  }

  recordSummary(rec: RegistryRecord): string {
    return [rec.status, rec.entityType, rec.jurisdiction, rec.registrationNumber ? `reg ${rec.registrationNumber}` : null,
      rec.incorporationDate ? `inc. ${rec.incorporationDate}` : null, rec.address].filter(Boolean).join(' · ');
  }
}
