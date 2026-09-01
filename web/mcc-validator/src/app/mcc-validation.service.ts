import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
import { environment } from '../environments/environment';
import { MccCatalogItem, MccValidationRequest, MccValidationResult } from './mcc-validation.models';

@Injectable({ providedIn: 'root' })
export class MccValidationService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/api/mcc-validation`;

  readonly catalog$: Observable<MccCatalogItem[]> = this.http
    .get<MccCatalogItem[]>(`${this.baseUrl}/catalog`)
    .pipe(shareReplay(1));

  validate(request: MccValidationRequest): Observable<MccValidationResult> {
    return this.http.post<MccValidationResult>(`${this.baseUrl}/validate`, request);
  }
}
